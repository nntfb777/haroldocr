@echo off
rem ============================================================
rem  Dong goi TrayOcrSnipper thanh THU MUC chay duoc (kem san .NET)
rem  Ket qua: thu muc "publish" nam canh file nay
rem  Luu y: KHONG dong goi thanh 1 file .exe vi Tesseract can cac DLL
rem  di kem (thu muc x64). Hay copy NGUYEN thu muc "publish" sang may khac.
rem ============================================================
cd /d "%~dp0"

dotnet publish TrayOcrSnipper.csproj -c Release -r win-x64 --self-contained true ^
  -p:DebugType=none ^
  -o "%~dp0publish"

if errorlevel 1 (
  echo.
  echo === DONG GOI THAT BAI. Xem thong bao loi o tren. ===
  pause
  exit /b 1
)

echo.
echo === THANH CONG ===
echo Thu muc: %~dp0publish
echo Chay file: %~dp0publish\Harold.exe
pause