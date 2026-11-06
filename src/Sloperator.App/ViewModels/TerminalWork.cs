namespace Sloperator.App.ViewModels;

/// <summary>
/// Whether a run_shell command is work the Terminal is worth opening for under Auto: a server's, an install, a
/// build, tests, a program or a script being run; a command that only looks around (cd, ls, cat, grep, git status)
/// is not (the user, 2026-11-06). By the program each command in the line runs, for every model and system alike.
/// </summary>
public static class TerminalWork
{
    /// <summary>The programs that are such work, whatever they are told. A program not here is left out: looking around has no end of commands, this work has few.</summary>
    private static readonly HashSet<string> Tools = new(StringComparer.OrdinalIgnoreCase)
    {
        // On another machine.
        "ssh", "scp", "sftp", "rsync", "telnet", "mosh", "plink", "pscp", "Invoke-Command", "Enter-PSSession", "New-PSSession",
        // Keeping a server.
        "systemctl", "service", "journalctl", "crontab", "ufw", "iptables", "firewall-cmd", "nginx", "apachectl", "caddy", "certbot", "iisreset", "pm2",
        "Start-Service", "Stop-Service", "Restart-Service", "Set-Service", "psql", "mysql", "sqlcmd", "redis-cli", "mongosh",
        "docker", "docker-compose", "podman", "kubectl", "helm", "terraform", "ansible", "ansible-playbook", "vagrant", "aws", "az", "gcloud",
        // Installing.
        "apt", "apt-get", "yum", "dnf", "pacman", "apk", "brew", "choco", "winget", "scoop", "pip", "pip3", "pipx", "uv", "poetry", "conda",
        "npm", "npx", "pnpm", "yarn", "bun", "gem", "composer", "nuget", "vcpkg", "Install-Module", "Install-Package", "Install-WindowsFeature",
        // Building and testing.
        "dotnet", "msbuild", "devenv", "cmake", "configure", "make", "nmake", "ninja", "gcc", "g++", "clang", "clang++", "cl", "cargo", "rustc", "go",
        "javac", "java", "mvn", "mvnw", "gradle", "gradlew", "ant", "tsc", "vite", "webpack", "esbuild", "rollup", "next", "unity", "godot",
        "pytest", "tox", "jest", "vitest", "mocha", "playwright", "cypress", "rspec", "phpunit", "gh",
    };

    /// <summary>Programs that run a program: work, unless told a line to run there and then (python -c "print(2 + 2)"), which is how the agent reads a spreadsheet.</summary>
    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase) { "python", "python3", "py", "node", "deno", "ruby", "perl", "php" };

    /// <summary>Words that run what comes after them: what they run says whether it is work (powershell -File build.ps1, sudo systemctl restart nginx).</summary>
    private static readonly HashSet<string> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "pwsh", "cmd", "bash", "sh", "zsh", "wsl", "sudo", "doas", "env", "time", "nohup", "nice", "exec", "call", "start", "Start-Process",
    };

    /// <summary>Scripts, which are run as programs whatever their name.</summary>
    private static readonly HashSet<string> Scripts = new(StringComparer.OrdinalIgnoreCase) { ".ps1", ".sh", ".bat", ".cmd" };

    /// <summary>What a program's first word asks when it only looks (node --version, pip list, npm view react): the agent checks what is there all the time.</summary>
    private static readonly HashSet<string> Looks = new(StringComparer.OrdinalIgnoreCase)
    {
        "--version", "-version", "-v", "--help", "-h", "/?", "--info", "version", "help", "info", "list", "ls", "show", "view", "search",
    };

    /// <summary>The git commands that are work: the ones that talk to a server. The rest read the project or change it in a moment (git status, git diff, git add).</summary>
    private static readonly HashSet<string> GitWork = new(StringComparer.OrdinalIgnoreCase) { "clone", "pull", "push", "fetch" };

    public static bool Is(string? command) => command is not null && Commands(command).Any(IsWork);

    /// <summary>The commands in a line, each as its words without their quotes: split at ; &amp; | and line breaks, and inside brackets (a block, $(...)), not inside quotes.</summary>
    private static List<List<string>> Commands(string line)
    {
        var commands = new List<List<string>>();
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        var quote = '\0';
        void EndWord()
        {
            if (word.Length > 0)
                words.Add(word.ToString());
            word.Clear();
        }
        void EndCommand()
        {
            EndWord();
            if (words.Count > 0)
                commands.Add(words);
            words = [];
        }
        foreach (var c in line)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    word.Append(c);
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c is ';' or '&' or '|' or '\n' or '\r' or '(' or ')' or '{' or '}')
                EndCommand();
            else if (char.IsWhiteSpace(c))
                EndWord();
            else
                word.Append(c);
        }
        EndCommand();
        return commands;
    }

    private static bool IsWork(List<string> words)
    {
        // What a variable is given is the command: $out = dotnet build, CC=clang make.
        var first = 0;
        while (first < words.Count && (words[first].Contains('=') || (first + 1 < words.Count && words[first + 1] == "=")))
            first += words[first].Contains('=') ? 1 : 2;
        if (first >= words.Count)
            return false;

        var program = words[first];
        var name = program[(program.LastIndexOfAny(['\\', '/']) + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        var rest = words.Skip(first + 1).ToList();
        if (Runners.Contains(name))
            return rest.Any(Is);
        if (rest.Count > 0 && Looks.Contains(rest[0]))
            return false;
        if (name.Equals("git", StringComparison.OrdinalIgnoreCase))
            return rest.Any(GitWork.Contains);
        if (Interpreters.Contains(name))
            return rest.Count == 0 || rest[0] is not ("-c" or "-e");
        return Tools.Contains(name) || Scripts.Contains(Path.GetExtension(name));
    }
}
