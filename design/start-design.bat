@echo off
cd /d "%~dp0"

if not exist "node_modules" (
  echo Installing prototype dependencies...
  call npm install
  if errorlevel 1 pause & exit /b 1
)

call npm run dev -- --host 127.0.0.1 --port 4173 --open /app.html
