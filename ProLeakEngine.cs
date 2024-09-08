/*
    ProLeak Engine
    Copyright (C) 2024  Alexandre 'kidev' Poumaroux

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.Serialization;
// ReSharper disable SuggestVarOrType_BuiltInTypes
// ReSharper disable SuggestVarOrType_SimpleTypes

namespace ProLeak;

[BepInPlugin("org.kidev.ltd2.proleak.engine", "ProLeak", "1.0.0")]
[BepInProcess("LegionTD2.exe")]
[BepInProcess("LegionTD2")]
[BepInProcess("LegionTD2.app")]
public class ProLeakEngine : BaseUnityPlugin
{
    private static ManualLogSource _logger;
    private static TcpListener _tcpListener;
    private static readonly List<TcpClient> Clients = [];
    private static readonly object ClientLock = new object();
    private static readonly HashSet<string> IgnoredNamespaces = ["System", "UnityEngine"];
    private static bool _isSharing = false;
    private static readonly ManualResetEvent InterceptorResponseEvent = new ManualResetEvent(false);
    private static Dictionary<string, object> _interceptorResponse;
    private static readonly HashSet<string> RegisteredInterceptors = [];
    
    [Serializable]
    private class InterceptionResponse
    {
        [FormerlySerializedAs("m_event")] [SerializeField]
        private string mEvent;
        public string Event { get => mEvent; set => mEvent = value; }

        [FormerlySerializedAs("m_params")] [SerializeField]
        private ParamsWrapper mParams;
        public ParamsWrapper Params { get => mParams; set => mParams = value; }
    }

    [Serializable]
    private class ParamsWrapper
    {
        [FormerlySerializedAs("m_entries")] [SerializeField]
        private List<ParamEntry> mEntries;
        public List<ParamEntry> Entries { get => mEntries; set => mEntries = value; }
    }

    [Serializable]
    private class ParamEntry
    {
        [FormerlySerializedAs("m_key")] [SerializeField]
        private string mKey;
        public string Key { get => mKey; set => mKey = value; }

        [FormerlySerializedAs("m_value")] [SerializeField]
        private string mValue;
        public string Value { get => mValue; set => mValue = value; }
    }

    public void Awake()
    {
        _logger = Logger;
        _logger.LogInfo($"[PROLEAK] ProLeak Engine v1.0.0 is loaded!");

        // Start TCP server
        Thread serverThread = new Thread(StartServer);
        serverThread.Start();

        // Apply patches to all non-system types
        var harmony = new Harmony("org.kidev.ltd2.proleak.engine");
        PatchAllTypes(harmony);

        // Set up Unity message interception
        GameObject interceptor = new GameObject("EventInterceptor");
        interceptor.AddComponent<UnityMessageInterceptor>();
        DontDestroyOnLoad(interceptor);
    }

    private void StartServer()
    {
        _tcpListener = new TcpListener(System.Net.IPAddress.Loopback, 69420);
        _tcpListener.Start();
        _logger.LogInfo("[PROLEAK] ProLeak Engine is ready!");

        while (true)
        {
            TcpClient client = _tcpListener.AcceptTcpClient();
            Thread clientThread = new Thread(HandleClient);
            clientThread.Start(client);
        }
    }

    private void HandleClient(object clientObj)
    {
        TcpClient client = (TcpClient)clientObj;
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[1024];
        
        try
        {
            while (true)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                string message = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                if (message.StartsWith("INTERCEPTION_RESULT:"))
                {
                    string json = message.Substring(20);
                    InterceptionResponse response = JsonUtility.FromJson<InterceptionResponse>(json);
                    
                    // Convert ParamsWrapper to Dictionary<string, object>
                    _interceptorResponse = response.Params.Entries.ToDictionary(
                        entry => entry.Key,
                        entry => (object)entry.Value
                    );

                    InterceptorResponseEvent.Set();
                }
                else if (message.StartsWith("REGISTER_INTERCEPTOR:"))
                {
                    string methodName = message.Substring(21).Trim();
                    RegisteredInterceptors.Add(methodName);
                }
                else if (message.StartsWith("UNREGISTER_INTERCEPTOR:"))
                {
                    string methodName = message.Substring(23).Trim();
                    RegisteredInterceptors.Remove(methodName);
                }
                if (message.StartsWith("START"))
                {
                    lock (ClientLock)
                    {
                        Clients.Add(client);
                        _isSharing = true;
                    }
                    _logger.LogInfo("[PROLEAK] Event leakage started");
                }
                else if (message.StartsWith("STOP"))
                {
                    lock (ClientLock)
                    {
                        Clients.Remove(client);
                        _isSharing = Clients.Count > 0;
                    }
                    _logger.LogInfo("[PROLEAK] Event leakage stopped");
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError($"[PROLEAK] Error handling client: {e.Message}");
        }
        finally
        {
            lock (ClientLock)
            {
                Clients.Remove(client);
                _isSharing = Clients.Count > 0;
            }
            client.Close();
        }
    }

    private void OnDestroy()
    {
        _tcpListener?.Stop();
        lock (ClientLock)
        {
            foreach (var client in Clients)
            {
                client.Close();
            }
            Clients.Clear();
        }
    }

    private void PatchAllTypes(Harmony harmony)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (IgnoredNamespaces.All(ns => type.Namespace?.StartsWith(ns) != true))
                    {
                        PatchType(harmony, type);
                    }
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // Skip assemblies that can't be loaded
            }
        }
    }

    private void PatchType(Harmony harmony, Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            try
            {
                if (!method.IsAbstract && !method.ContainsGenericParameters)
                {
                    harmony.Patch(method, 
                        prefix: new HarmonyMethod(typeof(ProLeakEngine).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)),
                        postfix: new HarmonyMethod(typeof(ProLeakEngine).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[PROLEAK] Failed to patch method {method.Name} in type {type.FullName}: {ex.Message}");
            }
        }
    }
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private static bool Prefix(MethodBase __originalMethod, ref object __instance, ref object[] __args)
    {
        string methodName = __originalMethod.Name;
        if (!_isSharing || !RegisteredInterceptors.Contains(methodName)) return true;
        var eventParams = new Dictionary<string, object>
        {
            { "Type", "Interceptor" },
            { "Method", methodName },
            { "DeclaringType", __originalMethod.DeclaringType?.FullName },
            { "Instance", __instance?.GetType().Name },
            { "Arguments", __args.Select(a => a?.ToString() ?? "null").ToArray() },
        };

        SendEvent("Interceptor", eventParams);

        // Wait for response from Python
        InterceptorResponseEvent.Reset();
        if (!InterceptorResponseEvent.WaitOne(TimeSpan.FromSeconds(5))) return true; // 5 second timeout
        if (_interceptorResponse == null)
        {
            return false;  // Block the method call
        }

        // Update __args with modified parameters
        var args = _interceptorResponse["params"] as Dictionary<string, object>;
        for (int i = 0; i < __args.Length; i++)
        {
            if (args != null && args.ContainsKey($"arg{i}"))
            {
                __args[i] = Convert.ChangeType(args[$"arg{i}"], __args[i].GetType());
            }
        }
        return true;
    }

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private static void Postfix(MethodBase __originalMethod, object __instance, object[] __args, object __result)
    {
        if (_isSharing)
        {
            SendEvent("MethodCall", new Dictionary<string, object>
            {
                { "Type", "Postfix" },
                { "Method", __originalMethod.Name },
                { "DeclaringType", __originalMethod.DeclaringType?.FullName },
                { "Instance", __instance?.GetType().Name },
                { "Arguments", __args.Select(a => a?.ToString() ?? "null").ToArray() },
                { "Result", __result?.ToString() ?? "void" }
            });
        }
    }

    internal static void SendEvent(string eventName, Dictionary<string, object> eventData)
    {
        string eventString = $"Event: {eventName}\n";
        eventString = eventData.Aggregate(eventString, (current, kvp) => current + $"{kvp.Key}: {kvp.Value}\n");
        eventString += "---\n";

        byte[] eventBytes = System.Text.Encoding.UTF8.GetBytes(eventString);

        lock (ClientLock)
        {
            foreach (var client in Clients.ToList())
            {
                try
                {
                    client.GetStream().Write(eventBytes, 0, eventBytes.Length);
                }
                catch (Exception)
                {
                    Clients.Remove(client);
                    client.Close();
                }
            }
        }
    }
}

public class UnityMessageInterceptor : MonoBehaviour
{
    private static readonly string[] UnityMessages =
    [
        "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "OnEnable", "OnDisable",
        "OnDestroy", "OnTriggerEnter", "OnTriggerExit", "OnCollisionEnter", "OnCollisionExit"
    ];

    private void Start()
    {
        foreach (var methodName in UnityMessages)
        {
            gameObject.AddComponent<UnityMessageProxy>().methodName = methodName;
        }
    }
}

public class UnityMessageProxy : MonoBehaviour
{
    [FormerlySerializedAs("MethodName")] public string methodName;

    private void Start() => InvokeRepeating(nameof(InvokeMethod), 0, 0.1f);

    private void InvokeMethod()
    {
        var method = GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        method?.Invoke(this, null);
    }

    // Define all Unity message methods
    private void Awake() => LogMessage("Awake");
    private void Update() => LogMessage("Update");
    private void FixedUpdate() => LogMessage("FixedUpdate");
    private void LateUpdate() => LogMessage("LateUpdate");
    private void OnEnable() => LogMessage("OnEnable");
    private void OnDisable() => LogMessage("OnDisable");
    private void OnDestroy() => LogMessage("OnDestroy");
    private void OnTriggerEnter(Collider other) => LogMessage("OnTriggerEnter", other);
    private void OnTriggerExit(Collider other) => LogMessage("OnTriggerExit", other);
    private void OnCollisionEnter(Collision collision) => LogMessage("OnCollisionEnter", collision);
    private void OnCollisionExit(Collision collision) => LogMessage("OnCollisionExit", collision);

    private static void LogMessage(string method, object arg = null)
    {
        ProLeakEngine.SendEvent("UnityMessage", new Dictionary<string, object>
        {
            { "Method", method },
            { "Argument", arg?.ToString() ?? "null" }
        });
    }
}