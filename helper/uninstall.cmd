@echo off
rem Removes the Chrome registration and stops the duck. The extension keeps working without it.
taskkill /im KillingDuck.exe /f >nul 2>&1
reg delete "HKCU\Software\Google\Chrome\NativeMessagingHosts\com.killingduck.tabstrip" /f
echo The tab strip duck has been removed.
pause
