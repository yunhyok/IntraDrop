using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Runs Windows' actual Send To menu on a selection, in a separate STA process.
class ShellMenuProbe
{
    static readonly StringBuilder MenuTrace = new StringBuilder();
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr bind, [MarshalAs(UnmanagedType.LPWStr)] string name, ref uint eaten, out IntPtr pidl, ref uint attributes);
        void EnumObjects(IntPtr hwnd, uint flags, out IntPtr result);
        void BindToObject(IntPtr pidl, IntPtr bind, ref Guid iid, out IntPtr result);
        void BindToStorage(IntPtr pidl, IntPtr bind, ref Guid iid, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr param, IntPtr a, IntPtr b);
        void CreateViewObject(IntPtr hwnd, ref Guid iid, out IntPtr result);
        void GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex=0)] IntPtr[] items, ref uint attributes);
        void GetUIObjectOf(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex=1)] IntPtr[] items, ref Guid iid, IntPtr reserved, out IntPtr result);
    }
    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr text, uint length);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
    struct InvokeInfo
    {
        public int size; public uint mask; public IntPtr hwnd; public IntPtr verb;
        public string parameters; public string directory; public int show; public uint hotkey; public IntPtr icon;
    }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint attributes, out uint actual);
    [DllImport("shell32.dll")] static extern int SHBindToParent(IntPtr pidl, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder folder, out IntPtr child);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")] static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetMenuString(IntPtr menu, uint position, StringBuilder text, int maximum, uint flags);

    static uint Find(IContextMenu2 context, IntPtr menu, string target)
    {
        for (int i=0; i<GetMenuItemCount(menu); i++)
        {
            var text = new StringBuilder(512); GetMenuString(menu,(uint)i,text,text.Capacity,0x400);
            MenuTrace.AppendLine(text.ToString());
            IntPtr sub = GetSubMenu(menu,i);
            if (sub!=IntPtr.Zero)
            {
                int result=context.HandleMenuMsg(0x117,sub,new IntPtr(i));
                MenuTrace.AppendLine("Popup result: " + result.ToString("X8"));
                uint found=Find(context,sub,target); if (found!=0) return found;
            }
            else if (text.ToString().Replace("&", "")==target) return GetMenuItemID(menu,i);
        }
        return 0;
    }
    [STAThread]
    static int Main(string[] args)
    {
        var pidls=new IntPtr[args.Length-1]; var children=new IntPtr[pidls.Length];
        IShellFolder folder=null; IContextMenu2 context=null; IntPtr pointer=IntPtr.Zero, menu=IntPtr.Zero;
        try
        {
            Guid sf=typeof(IShellFolder).GUID;
            for(int i=0;i<pidls.Length;i++)
            {
                uint attrs; Marshal.ThrowExceptionForHR(SHParseDisplayName(Path.GetFullPath(args[i+1]),IntPtr.Zero,out pidls[i],0,out attrs));
                IShellFolder parent; Marshal.ThrowExceptionForHR(SHBindToParent(pidls[i],ref sf,out parent,out children[i]));
                if(i==0) folder=parent; else Marshal.ReleaseComObject(parent);
            }
            Guid cm=new Guid("000214E4-0000-0000-C000-000000000046");
            folder.GetUIObjectOf(IntPtr.Zero,(uint)children.Length,children,ref cm,IntPtr.Zero,out pointer);
            context=(IContextMenu2)Marshal.GetObjectForIUnknown(pointer); menu=CreatePopupMenu();
            Marshal.ThrowExceptionForHR(context.QueryContextMenu(menu,0,1,0x7fff,0));
            uint id=Find(context,menu,args[0]); if(id==0) throw new Exception("Send To entry not found: " + args[0] + "\n" + MenuTrace);
            var info=new InvokeInfo {size=Marshal.SizeOf(typeof(InvokeInfo)),mask=0x100,verb=new IntPtr(id-1),show=0};
            Marshal.ThrowExceptionForHR(context.InvokeCommand(ref info)); return 0;
        }
        catch(Exception ex) {Console.Error.WriteLine(ex); return 1;}
        finally
        {
            if(menu!=IntPtr.Zero) DestroyMenu(menu);
            if(context!=null) Marshal.ReleaseComObject(context);
            if(pointer!=IntPtr.Zero) Marshal.Release(pointer);
            if(folder!=null) Marshal.ReleaseComObject(folder);
            foreach(IntPtr pidl in pidls) if(pidl!=IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }
}
