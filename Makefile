all: clean build

clean:
	@dotnet clean ProLeakEngine.csproj

build:
	@dotnet build ProLeakEngine.csproj
