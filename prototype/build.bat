@echo off
rem Сборка «Конструктор шаблонов.exe» (нужны Python 3.10+, pip install python-docx pyinstaller)
chcp 65001 >nul
cd /d "%~dp0"
python -m PyInstaller --noconfirm --onefile --windowed --name ShablonRabot ^
  --icon "%cd%\icon.ico" --add-data "%cd%\config;config" --add-data "%cd%\icon.png;." ^
  --collect-data docx --workpath build --specpath build --distpath dist app.py || exit /b 1
move /y "dist\ShablonRabot.exe" "dist\Конструктор шаблонов.exe" >nul
echo Готово: dist\Конструктор шаблонов.exe
