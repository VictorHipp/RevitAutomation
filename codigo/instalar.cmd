@echo off
rem Doble clic para compilar e instalar RevitAutomation en Revit 2023, 2024, 2025, 2026 y 2027.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0instalar.ps1" %*
pause
