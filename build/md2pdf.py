# -*- coding: utf-8 -*-
"""Markdown -> PDF 转换脚本。
方案：Markdown -> HTML（带中文字体与排版样式）-> 调用 Edge headless 转 PDF。
用法：python md2pdf.py <input.md> <output.pdf>
"""
import sys
import re
import html
import subprocess
import os
import tempfile

# 常见 Windows Edge 路径
EDGE_PATHS = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]


def find_edge():
    for p in EDGE_PATHS:
        if os.path.exists(p):
            return p
    return None


def inline(text):
    """把行内 Markdown（**加粗**、`代码`）转成 HTML，并转义。"""
    out = []
    # 先按 **加粗** 拆分
    parts = re.split(r'(\*\*[^*]+\*\*)', text)
    for part in parts:
        if not part:
            continue
        if part.startswith('**') and part.endswith('**'):
            out.append('<strong>' + html.escape(part[2:-2]) + '</strong>')
        else:
            # 处理 `行内代码`
            segs = re.split(r'(`[^`]+`)', part)
            for seg in segs:
                if not seg:
                    continue
                if seg.startswith('`') and seg.endswith('`'):
                    out.append('<code>' + html.escape(seg[1:-1]) + '</code>')
                else:
                    out.append(html.escape(seg))
    return ''.join(out)


def md_to_html(md_path):
    with open(md_path, 'r', encoding='utf-8') as f:
        lines = f.read().split('\n')

    body = []
    i = 0
    n = len(lines)
    in_list = None  # 'ul' | 'ol'

    def close_list():
        nonlocal in_list
        if in_list:
            body.append('</' + in_list + '>')
            in_list = None

    while i < n:
        line = lines[i].rstrip()

        if not line.strip():
            close_list()
            i += 1
            continue

        # 分隔线
        if re.match(r'^\s*-{3,}\s*$', line):
            close_list()
            body.append('<hr>')
            i += 1
            continue

        # 标题
        m = re.match(r'^(#{1,4})\s+(.*)$', line)
        if m:
            close_list()
            level = len(m.group(1))
            text = inline(m.group(2).strip())
            body.append(f'<h{level}>{text}</h{level}>')
            i += 1
            continue

        # 表格
        if line.strip().startswith('|') and i + 1 < n and re.match(r'^\s*\|[\s:\-|]+\|\s*$', lines[i + 1]):
            close_list()
            header = [c.strip() for c in line.strip().strip('|').split('|')]
            i += 2
            rows = []
            while i < n and lines[i].strip().startswith('|'):
                rows.append([c.strip() for c in lines[i].strip().strip('|').split('|')])
                i += 1
            body.append('<table>')
            body.append('<thead><tr>' + ''.join(f'<th>{inline(h)}</th>' for h in header) + '</tr></thead>')
            body.append('<tbody>')
            for row in rows:
                body.append('<tr>' + ''.join(f'<td>{inline(c)}</td>' for c in row) + '</tr>')
            body.append('</tbody></table>')
            continue

        # 无序列表
        m = re.match(r'^\s*[-*]\s+(.*)$', line)
        if m:
            if in_list != 'ul':
                close_list()
                body.append('<ul>')
                in_list = 'ul'
            body.append('<li>' + inline(m.group(1).strip()) + '</li>')
            i += 1
            continue

        # 有序列表
        m = re.match(r'^\s*\d+\.\s+(.*)$', line)
        if m:
            if in_list != 'ol':
                close_list()
                body.append('<ol>')
                in_list = 'ol'
            body.append('<li>' + inline(m.group(1).strip()) + '</li>')
            i += 1
            continue

        # 引用
        m = re.match(r'^\s*>\s*(.*)$', line)
        if m:
            close_list()
            body.append('<blockquote>' + inline(m.group(1).strip()) + '</blockquote>')
            i += 1
            continue

        # 普通段落
        close_list()
        body.append('<p>' + inline(line.strip()) + '</p>')
        i += 1

    close_list()

    content = '\n'.join(body)

    # 完整 HTML，带中文字体与打印样式
    return f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<style>
  @page {{ margin: 20mm 18mm; }}
  body {{
    font-family: "Microsoft YaHei", "微软雅黑", "Segoe UI", sans-serif;
    font-size: 11pt;
    line-height: 1.7;
    color: #222;
    max-width: 780px;
    margin: 0 auto;
    padding: 24px 0;
  }}
  h1 {{
    font-size: 24pt;
    color: #1F3B63;
    text-align: center;
    border-bottom: 3px solid #1F3B63;
    padding-bottom: 12px;
    margin-bottom: 24px;
  }}
  h2 {{
    font-size: 16pt;
    color: #1F3B63;
    margin-top: 28px;
    border-left: 5px solid #1F3B63;
    padding-left: 10px;
  }}
  h3 {{ font-size: 13pt; color: #2E5A88; margin-top: 20px; }}
  h4 {{ font-size: 11.5pt; color: #2E5A88; margin-top: 16px; }}
  p {{ margin: 8px 0; }}
  ul, ol {{ margin: 8px 0; padding-left: 28px; }}
  li {{ margin: 4px 0; }}
  strong {{ color: #1F3B63; }}
  code {{
    font-family: Consolas, monospace;
    background: #f5f5f5;
    color: #C7254E;
    padding: 1px 5px;
    border-radius: 3px;
    font-size: 10pt;
  }}
  table {{
    border-collapse: collapse;
    width: 100%;
    margin: 14px 0;
    font-size: 10pt;
  }}
  th, td {{
    border: 1px solid #ccc;
    padding: 6px 10px;
    text-align: left;
  }}
  th {{ background: #1F3B63; color: #fff; font-weight: bold; }}
  tr:nth-child(even) td {{ background: #f7f9fc; }}
  blockquote {{
    border-left: 4px solid #bbb;
    margin: 12px 0;
    padding: 6px 16px;
    color: #666;
    background: #fafafa;
  }}
  hr {{ border: none; border-top: 1px solid #ddd; margin: 20px 0; }}
</style>
</head>
<body>
{content}
</body>
</html>"""


def html_to_pdf(html_content, pdf_path):
    edge = find_edge()
    if not edge:
        raise RuntimeError("未找到 Edge 浏览器，无法转 PDF")

    # 写临时 HTML
    tmp_html = os.path.join(tempfile.gettempdir(), '_md2pdf_temp.html')
    with open(tmp_html, 'w', encoding='utf-8') as f:
        f.write(html_content)

    # 用 Edge headless 打印成 PDF
    cmd = [
        edge,
        '--headless',
        '--disable-gpu',
        '--no-pdf-header-footer',
        '--print-to-pdf=' + os.path.abspath(pdf_path),
        'file:///' + tmp_html.replace('\\', '/'),
    ]
    subprocess.run(cmd, capture_output=True, timeout=120)


def convert(md_path, pdf_path):
    html_content = md_to_html(md_path)
    html_to_pdf(html_content, pdf_path)
    print(f"OK -> {pdf_path}")


if __name__ == '__main__':
    if len(sys.argv) != 3:
        print("用法：python md2pdf.py <input.md> <output.pdf>")
        sys.exit(1)
    convert(sys.argv[1], sys.argv[2])
