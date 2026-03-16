IF "%VERSION_PATCH%"=="" (
    SET VERSION=3.111.9
) ELSE (
    SET VERSION=10.0.%VERSION_PATCH%
)

echo %VERSION%

dotnet pack dotnet-ws.fsproj -o publish -c Release /p:Version=%VERSION%

@REM if %errorlevel% neq 0 exit /b %errorlevel%

@REM git tag %VERSION%

@REM if %errorlevel% neq 0 exit /b %errorlevel%

@REM git push origin --tags
