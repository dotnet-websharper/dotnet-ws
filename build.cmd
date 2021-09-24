IF "%VERSION_PATCH%"=="" (
    SET VERSION=0.1.0.0-preview1
) ELSE (
    SET VERSION=0.1.0.%VERSION_PATCH%-preview1
)

echo %VERSION%

dotnet pack dotnet-ws.fsproj -o publish -c Release /p:Version=%VERSION%