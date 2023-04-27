IF "%VERSION_PATCH%"=="" (
    SET VERSION=0.99994
) ELSE (
    SET VERSION=0.1.%VERSION_PATCH%
)

echo %VERSION%

dotnet pack dotnet-ws.fsproj -o publish -c Release /p:Version=%VERSION%