using System;
using System.IO;
using System.Windows;
using DesktopNook.Core;

namespace DesktopNook.App;

// 跨进程 OLE 拖出：把收纳项按 Windows 标准格式交给外部程序（浏览器、微信、资源管理器等）。
//
// 现有面板内拖动是鼠标捕获 + RenderTransform 的手势，不会进入 OLE 拖放循环，
// 因此外部程序完全收不到数据。这里只负责构造 DataObject：
//   * FileDrop / FileNameW  —— 绝大多数程序（微信、浏览器上传、资源管理器）识别
//   * UnicodeText / Text    —— 输入框、地址栏可接收的路径或网址文本
//   * UniformResourceLocatorW —— .url 网址快捷方式，浏览器可直接打开
//   * Preferred DropEffect  —— 固定 Copy，且 AllowedEffects 不含 Move
//
// 最后一条是安全约束：拖出的是 vault 内的托管文件，若允许 Move，资源管理器
// 可能把文件搬走，收纳数据与恢复清单就会失效。
internal static class ExternalDrag
{
    internal const DragDropEffects AllowedEffects = DragDropEffects.Copy | DragDropEffects.Link;

    internal static DataObject? Build(NookItem item,string managedPath)
    {
        if(item.Kind=="recycle-bin")return null;           // 系统入口没有实际文件
        if(string.IsNullOrWhiteSpace(managedPath))return null;
        if(!File.Exists(managedPath)&&!Directory.Exists(managedPath))return null;

        var data=new DataObject();
        data.SetData("Preferred DropEffect",new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        data.SetData(DataFormats.FileDrop,new[]{managedPath});
        // WPF 的 DataFormats 没有 FileNameW / FileName 常量，用 CFSTR 格式名；
        // 只给 Unicode 版本，ANSI 版本会被 WPF 按 UTF-16 序列化而读错。
        data.SetData("FileNameW",managedPath);

        var url=ReadShortcutUrl(managedPath);
        if(!string.IsNullOrWhiteSpace(url))
        {
            data.SetData("UniformResourceLocatorW",url);
            data.SetData("UniformResourceLocator",url);
        }
        var text=url??managedPath;
        data.SetData(DataFormats.UnicodeText,text);
        data.SetData(DataFormats.Text,text);
        return data;
    }

    // .url 是 INI 风格的 Internet Shortcut：取 [InternetShortcut] 下的 URL=。
    // 解析失败或非 .url 时返回 null，拖出仍按普通文件处理。
    private static string? ReadShortcutUrl(string path)
    {
        if(!File.Exists(path)||!Path.GetExtension(path).Equals(".url",StringComparison.OrdinalIgnoreCase))return null;
        try
        {
            foreach(var line in File.ReadLines(path))
            {
                var trimmed=line.Trim();
                if(!trimmed.StartsWith("URL=",StringComparison.OrdinalIgnoreCase))continue;
                var value=trimmed[4..].Trim();
                return value.Length>0?value:null;
            }
        }
        catch{/* 编码或权限异常时按普通文件处理 */}
        return null;
    }
}
