# -*- coding: utf-8 -*-
"""生成 GBK 编码的一键转换 bat 文件（供 Windows cmd 正确读取中文）。"""
import os

bat_content = r'''@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

rem ============================================
rem  Markdown 一键转 Word + PDF
rem  用法：把 .md 文件拖到这个 bat 上
rem ============================================

set "PY=python"
rem 上面这行默认用 PATH 里的 python；如果你的 python 不在 PATH，
rem 把它改成完整路径，例如 set "PY=D:\Python313\python.exe"
set "SCRIPT_DIR=%~dp0"

if "%~1"=="" (
    echo 用法：把 .md 文件拖到这个脚本图标上即可。
    echo 会自动在同目录生成同名的 .docx 和 .pdf 文件。
    pause
    exit /b
)

:loop
if "%~1"=="" goto end

set "MD_FILE=%~f1"
set "BASE_NAME=%~dpn1"

echo.
echo 正在转换: %~nx1

"%PY%" "%SCRIPT_DIR%md2docx.py" "%MD_FILE%" "%BASE_NAME%.docx"
if errorlevel 1 (echo [失败] Word 转换出错) else (echo [完成] 已生成 docx)

"%PY%" "%SCRIPT_DIR%md2pdf.py" "%MD_FILE%" "%BASE_NAME%.pdf"
if errorlevel 1 (echo [失败] PDF 转换出错) else (echo [完成] 已生成 pdf)

shift
goto loop

:end
echo 全部转换完成！
pause
'''

out_path = os.path.join(os.path.dirname(__file__), 'md转word和pdf.bat')
with open(out_path, 'w', encoding='gbk') as f:
    f.write(bat_content)
print('已生成 GBK 编码的 bat:', out_path)
