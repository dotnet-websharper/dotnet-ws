IF "%VERSION_PATCH%"=="" (
    SET VERSION=3.111.9
) ELSE (
    SET VERSION=0.1.%VERSION_PATCH%
)

echo %VERSION%

dotnet pack dotnet-ws.fsproj -o publish -c Release /p:Version=%VERSION%

if %errorlevel% neq 0 exit /b %errorlevel%

git tag %VERSION%

if %errorlevel% neq 0 exit /b %errorlevel%

git push origin --tags