@echo off
title Madorin Runtime Server
set MADORIN_HANDSHAKE_SECRET=test-secret-dev
cd /d "E:\Netor.me\Madorin\Netor.Madorin\Src\Madorin.Ai.Runtime"
echo Starting Madorin server...
dotnet run --project src/Madorin.AI.Runtime.Cli -- serve --workspace "%~dp0test-workspace"
pause
