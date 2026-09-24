using System;

// Which server to talk to. Normally the compiled-in address, but a developer can point the client at
// another one (e.g. a local server) with `-server host` / `-port n` on the command line or the
// BLOB_SERVER environment variable - used by the automated test player.
public static class ServerAddress
{
    public static string Host(string defaultHost)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-server") return args[i + 1];
        }

        string env = Environment.GetEnvironmentVariable("BLOB_SERVER");
        return string.IsNullOrEmpty(env) ? defaultHost : env;
    }

    public static int Port(int defaultPort)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            int parsed;
            if (args[i] == "-port" && int.TryParse(args[i + 1], out parsed)) return parsed;
        }
        return defaultPort;
    }
}
