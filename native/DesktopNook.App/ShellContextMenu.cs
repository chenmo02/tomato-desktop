using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DesktopNook.Core;

namespace DesktopNook.App;
internal sealed class ShellContextMenu : IDisposable
{
    [ComImport,Guid("000214E6-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]int ParseDisplayName(IntPtr h,IntPtr b,[MarshalAs(UnmanagedType.LPWStr)]string s,ref uint eaten,out IntPtr p,ref uint attrs);
        [PreserveSig]int EnumObjects(IntPtr h,uint flags,out IntPtr e);
        [PreserveSig]int BindToObject(IntPtr p,IntPtr b,ref Guid id,out IntPtr result);
        [PreserveSig]int BindToStorage(IntPtr p,IntPtr b,ref Guid id,out IntPtr result);
        [PreserveSig]int CompareIDs(IntPtr flags,IntPtr a,IntPtr b);
        [PreserveSig]int CreateViewObject(IntPtr h,ref Guid id,out IntPtr result);
        [PreserveSig]int GetAttributesOf(uint count,[MarshalAs(UnmanagedType.LPArray,SizeParamIndex=0)]IntPtr[] ids,ref uint attrs);
        [PreserveSig]int GetUIObjectOf(IntPtr h,uint count,[MarshalAs(UnmanagedType.LPArray,SizeParamIndex=1)]IntPtr[] ids,ref Guid iid,IntPtr reserved,out IntPtr result);
    }
    [ComImport,Guid("000214E4-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]int QueryContextMenu(IntPtr menu,uint index,uint first,uint last,uint flags);
        [PreserveSig]int InvokeCommand(ref InvokeInfo info);
        [PreserveSig]int GetCommandString(UIntPtr command,uint flags,IntPtr reserved,[MarshalAs(UnmanagedType.LPWStr)]StringBuilder text,uint length);
    }
    [ComImport,Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig]int QueryContextMenu(IntPtr menu,uint index,uint first,uint last,uint flags);
        [PreserveSig]int InvokeCommand(ref InvokeInfo info);
        [PreserveSig]int GetCommandString(UIntPtr command,uint flags,IntPtr reserved,[MarshalAs(UnmanagedType.LPWStr)]StringBuilder text,uint length);
        [PreserveSig]int HandleMenuMsg(uint msg,IntPtr wp,IntPtr lp);
        [PreserveSig]int HandleMenuMsg2(uint msg,IntPtr wp,IntPtr lp,out IntPtr result);
    }
    [StructLayout(LayoutKind.Sequential)]private struct InvokeInfo {public uint Size,Mask;public IntPtr Owner,Verb,Parameters,Directory;public int Show;public uint Hotkey;public IntPtr Icon;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHParseDisplayName(string s,IntPtr b,out IntPtr p,uint attrs,out uint found);
    [DllImport("shell32.dll")]private static extern int SHBindToParent(IntPtr p,ref Guid id,[MarshalAs(UnmanagedType.Interface)]out IShellFolder folder,out IntPtr child);
    [DllImport("user32.dll")]private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")]private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetMenuString(IntPtr menu,uint item,StringBuilder text,int max,uint flags);
    [DllImport("user32.dll")]private static extern uint GetMenuItemID(IntPtr menu,int position);
    [DllImport("user32.dll")]private static extern uint GetMenuState(IntPtr menu,uint item,uint flags);
    [DllImport("user32.dll")]private static extern IntPtr GetSubMenu(IntPtr menu,int position);
    [DllImport("user32.dll")]private static extern uint TrackPopupMenuEx(IntPtr menu,uint flags,int x,int y,IntPtr owner,IntPtr parameters);
    private readonly IContextMenu _context;private readonly IContextMenu3? _extended;private readonly IntPtr _menu;private readonly IntPtr _owner;
    private bool _disposed;
    private int _busy;private bool _disposeRequested;
    public ShellContextMenu(Window owner,NookStore store,NookItem item)
    {
        _owner=new WindowInteropHelper(owner).Handle;
        var path="::"+WindowsRecycleBin.RecycleId;
        if(item.Kind!="recycle-bin")
        {
            // Extensions operate on a presentation copy, never the sole managed file.
            if(item.Kind is "file" or "folder")path=store.ManagedPath(item);
            else{var dir=Path.Combine(store.DataDirectory,"shell-menu",item.Id);Directory.CreateDirectory(dir);path=Path.Combine(dir,item.FileName);File.Copy(store.ManagedPath(item),path,true);}
        }
        Marshal.ThrowExceptionForHR(SHParseDisplayName(path,IntPtr.Zero,out var pidl,0,out _));
        try
        {
            var folderId=typeof(IShellFolder).GUID;Marshal.ThrowExceptionForHR(SHBindToParent(pidl,ref folderId,out var folder,out var child));
            try
            {
                var iid=typeof(IContextMenu).GUID;Marshal.ThrowExceptionForHR(folder.GetUIObjectOf(_owner,1,[child],ref iid,IntPtr.Zero,out var ptr));
                try{_context=(IContextMenu)Marshal.GetObjectForIUnknown(ptr);}finally{Marshal.Release(ptr);}
            }finally{Marshal.ReleaseComObject(folder);}
        }finally{Marshal.FreeCoTaskMem(pidl);}
        _extended=_context as IContextMenu3;_menu=CreatePopupMenu();
        Marshal.ThrowExceptionForHR(_context.QueryContextMenu(_menu,0,1,0x6fff,0x10));
    }
    public string Verb(uint command)
    {
        var text=new StringBuilder(256);return _context.GetCommandString((UIntPtr)(command-1),4,IntPtr.Zero,text,256)>=0?text.ToString():"";
    }
    public List<object> Build(Action<string,Action> execute)=>Read(_menu,execute,0);
    internal List<string> RootVerbs(){var verbs=new List<string>();for(var i=0;i<GetMenuItemCount(_menu);i++){var id=GetMenuItemID(_menu,i);if(id>0&&id<0x7000)verbs.Add(Verb(id));}return verbs;}
    internal void InvokeVerb(string verb){for(var i=0;i<GetMenuItemCount(_menu);i++){var id=GetMenuItemID(_menu,i);if(id>0&&id<0x7000&&Verb(id)==verb){Invoke(id);return;}}throw new IOException("系统菜单没有此命令："+verb);}
    private List<object> Read(IntPtr menu,Action<string,Action> execute,int depth)
    {
        var result=new List<object>();if(depth>8)return result;
        for(var i=0;i<GetMenuItemCount(menu);i++)
        {
            var flags=GetMenuState(menu,(uint)i,0x400);if((flags&0x800)!=0){result.Add(new Separator());continue;}
            var text=new StringBuilder(1024);GetMenuString(menu,(uint)i,text,text.Capacity,0x400);
            // Shell 的 &O 助记符会变成「打开(O)」；去掉尾部单字母括号，\t 后面是快捷键。
            var label=Regex.Replace(text.ToString().Replace("&",""),@"\s*\([A-Za-z0-9]\)\s*$","");
            var parts=label.Split('\t');label=parts[0].Trim();if(string.IsNullOrWhiteSpace(label))continue;
            var gesture=parts.Length>1?parts[1].Trim():"";
            var command=GetMenuItemID(menu,i);var child=GetSubMenu(menu,i);var item=new MenuItem{Header=label,InputGestureText=gesture,IsEnabled=(flags&3)==0,IsCheckable=(flags&8)!=0,IsChecked=(flags&8)!=0};
            // 把 Shell 的规范动词（open / runas / properties …）带给菜单层，用于跨语言识别常用动作。
            item.Tag=command>0&&command<0x7000?Verb(command):"";
            if(child!=IntPtr.Zero)
            {
                item.Items.Add(new MenuItem{Header="…"});var position=i;
                item.SubmenuOpened+=(_,_)=>{_extended?.HandleMenuMsg(0x117,child,(IntPtr)position);item.Items.Clear();foreach(var entry in Read(child,execute,depth+1))item.Items.Add(entry);};
            }
            else if(command>0&&command<0x7000)item.Click+=(_,e)=>{e.Handled=true;execute(Verb(command),()=>Invoke(command));};
            result.Add(item);
        }
        return result;
    }
    private void Invoke(uint command)
    {
        _busy++;try{var info=new InvokeInfo{Size=(uint)Marshal.SizeOf<InvokeInfo>(),Owner=_owner,Verb=(IntPtr)(command-1),Show=1};Marshal.ThrowExceptionForHR(_context.InvokeCommand(ref info));}finally{EndBusy();}
    }
    public void FullMenu(Window window,Action<string,Action> execute)
    {
        var source=HwndSource.FromHwnd(_owner);
        _busy++;
        HwndSourceHook hook=(IntPtr h,int msg,IntPtr wp,IntPtr lp,ref bool handled)=>{if(_extended is not null && (msg is 0x117 or 0x2b or 0x2c or 0x120)){var hr=_extended.HandleMenuMsg2((uint)msg,wp,lp,out var value);if(hr>=0){handled=true;return value;}}return IntPtr.Zero;};
        source.AddHook(hook);
        try{var p=window.PointToScreen(System.Windows.Input.Mouse.GetPosition(window));var command=TrackPopupMenuEx(_menu,0x100|2,(int)p.X,(int)p.Y,_owner,IntPtr.Zero);if(command>0)execute(Verb(command),()=>Invoke(command));}
        finally{source.RemoveHook(hook);EndBusy();}
    }
    private void EndBusy(){_busy--;if(_busy==0&&_disposeRequested)Dispose();}
    public void Dispose(){if(_disposed)return;if(_busy>0){_disposeRequested=true;return;}_disposed=true;DestroyMenu(_menu);Marshal.ReleaseComObject(_context);}
}
