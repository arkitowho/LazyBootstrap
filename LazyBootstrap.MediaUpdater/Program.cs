using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LazyBootstrap.MediaUpdate;

namespace LazyBootstrap.MediaUpdater;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            string game = null, package = null, parent = null;
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
                int equals = name.IndexOf('=');
                string value;
                if (equals >= 0) { value = name[(equals + 1)..]; name = name[..equals]; }
                else if (++i < args.Length) value = args[i];
                else throw new IOException("缺少参数值：" + name);
                if (name == "--game" && game == null) game = value;
                else if (name == "--package" && package == null) package = value;
                else if (name == "--parent-pid" && parent == null) parent = value;
                else throw new IOException("更新程序参数无效或重复：" + name);
            }
            if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(package)
                || !int.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out int parentPid) || parentPid <= 0)
                throw new IOException("请从启动器选择更新包。内部用法：MediaUpdater.exe --game <游戏目录> --package <包目录> --parent-pid <启动器进程 ID>");
            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                int result = await MediaUpdateRunner.RunAsync(Path.GetFullPath(game), Path.GetFullPath(package), parentPid, Console.WriteLine, cancel.Token);
                if (result != 0 && !Console.IsInputRedirected)
                {
                    Console.WriteLine("按回车关闭此窗口。");
                    Console.ReadLine();
                }
                return result;
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
    }
}
