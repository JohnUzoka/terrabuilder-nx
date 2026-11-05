@echo off
rem terrabuilder for Windows: put this file in an empty folder and double-click it.
rem
rem It sets up WSL with Ubuntu (after asking), installs Podman, git, rsync and Python in
rem Ubuntu, keeps a copy of terrabuilder-nx in Ubuntu's home folder up to date and runs the
rem guided build there. Put your Terraria, tModLoader and .tmod files in the Terraria,
rem tModLoader and Mods folders it creates next to this file; finished builds and logs are
rem copied to the Output folder. Arguments go to terrabuilder: "terrabuilder.cmd doctor"
rem checks the machine. TERRABUILDER_DISTRO, TERRABUILDER_REPO and TERRABUILDER_BRANCH
rem override the WSL distribution, the repository and the branch of a new copy.
setlocal
if not defined TERRABUILDER_DISTRO set "TERRABUILDER_DISTRO=Ubuntu"
if not defined TERRABUILDER_REPO set "TERRABUILDER_REPO=https://github.com/JohnUzoka/terrabuilder-nx"
if not defined TERRABUILDER_BRANCH set "TERRABUILDER_BRANCH=main"
set "DISTRO=%TERRABUILDER_DISTRO%"
set "RC=0"

where wsl.exe >nul 2>&1
rem wsl.exe exits with -1 for its own errors, which "if errorlevel 1" (1 or more) misses.
if %errorlevel% neq 0 (
    echo This Windows has no WSL. terrabuilder needs Windows 10 version 2004 or later, or Windows 11.
    goto failed
)
wsl.exe -d %DISTRO% -e true >nul 2>&1
if %errorlevel% neq 0 goto install

:ready
echo Checking the Linux tools terrabuilder needs: Podman, git, rsync and Python.
rem Podman only when neither Podman nor a Docker that works without sudo is there.
set "ENGINE=podman"
wsl.exe -d %DISTRO% -e sh -c "command -v podman || docker info" >nul 2>&1
if %errorlevel% equ 0 set "ENGINE="
wsl.exe -d %DISTRO% -u root -e sh -c "need=; for tool in git rsync python3 %ENGINE%; do command -v $tool >/dev/null || need=$need' '$tool; done; case $need in '') exit 0;; esac; echo Installing$need; export DEBIAN_FRONTEND=noninteractive; (apt-get update -qq && apt-get install -y -qq$need) >/tmp/terrabuilder-apt.log 2>&1 || { tail -n 20 /tmp/terrabuilder-apt.log; exit 1; }"
if %errorlevel% neq 0 goto failed

wsl.exe -d %DISTRO% --cd ~ -e sh -c "if [ -d terrabuilder-nx/.git ]; then git -C terrabuilder-nx pull -q --ff-only || echo Could not update terrabuilder-nx, so the copy you have is used.; else echo Downloading terrabuilder-nx; git clone -q --branch %TERRABUILDER_BRANCH% %TERRABUILDER_REPO% terrabuilder-nx; fi"
if %errorlevel% neq 0 goto failed

wsl.exe -d %DISTRO% --cd ~ -e terrabuilder-nx/scripts/windows/launch.sh "%~dp0." %*
if %errorlevel% neq 0 goto failed
goto end

:install
echo terrabuilder builds inside WSL, the Windows Subsystem for Linux, with %DISTRO%,
echo which is not set up yet. Installing it needs administrator rights, and Windows may
echo ask you to restart afterwards.
set "ANSWER=y"
set /p "ANSWER=Install WSL and %DISTRO% now? [Y/n] "
if /i "%ANSWER%"=="yes" set "ANSWER=y"
if /i not "%ANSWER%"=="y" goto end
wsl.exe --install -d %DISTRO%
wsl.exe -d %DISTRO% -e true >nul 2>&1
if %errorlevel% equ 0 goto ready
echo.
echo If Windows asks you to restart, restart first. If %DISTRO% asked for a user name and a
echo password, create them; if it then shows a Linux prompt, type exit. Then double-click
echo terrabuilder.cmd again.
goto end

:failed
set "RC=1"
echo.
echo terrabuilder stopped with an error; the messages above say why. Build logs, if any,
echo are in "%~dp0Output\logs".

:end
echo.
pause
exit /b %RC%
