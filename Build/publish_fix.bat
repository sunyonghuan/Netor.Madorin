@echo off
set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%"
echo [*] PATH updated with vswhere dir
echo [*] Starting dotnet publish...
dotnet publish "E:\Netor.me\Cortana\Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj" -c Release -o "E:\Netor.me\Cortana\Realases\Cortana"
if %ERRORLEVEL% neq 0 (
    echo [FAIL] UI publish failed with exit code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)
echo [OK] UI published.
dotnet publish "E:\Netor.me\Cortana\Src\Plugins\Netor.Cortana.NativeHost\Netor.Cortana.NativeHost.csproj" -c Release -o "E:\Netor.me\Cortana\Realases\Cortana"
if %ERRORLEVEL% neq 0 (
    echo [FAIL] NativeHost publish failed with exit code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)
echo [OK] NativeHost published.
echo [DONE] All published successfully.
