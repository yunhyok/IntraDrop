using System;
using System.IO;
using System.Linq;
using System.Reflection;

class Capture
{
    static void Main(string[] args)
    {
        string exe = Assembly.GetExecutingAssembly().Location;
        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(exe), "invocations.log"), new[] { Path.GetFileName(exe) }.Concat(args));
    }
}
