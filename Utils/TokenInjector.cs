namespace MuxSwarm.Utils;

public static class TokenInjector
{
    public static string InjectTokens(string content)
    {
        content = content.Replace("{{paths.sandbox}}", App.Config.Filesystem.SandboxPath ?? "");
        content = content.Replace("{{paths.allowed}}", string.Join(", ", App.Config.Filesystem.AllowedPaths ?? []));
        content = content.Replace("{{paths.primary}}", App.Config.Filesystem.AllowedPaths?.FirstOrDefault() ?? "");
        content = content.Replace("{{paths.skills}}", App.Config.Filesystem.SkillsPath ?? "");
        content = content.Replace("{{paths.sessions}}", App.Config.Filesystem.SessionsPath ?? "");
        content = content.Replace("{{paths.base}}", PlatformContext.BaseDirectory);
        content = content.Replace("{{paths.config}}", PlatformContext.ConfigDirectory);
        content = content.Replace("{{paths.prompts}}", PlatformContext.PromptsDirectory);
        content = content.Replace("{{user}}", Environment.UserName);
        content = content.Replace("{{os}}", Common.GetOsFriendlyName());
        content = content.Replace("{{shell}}", PlatformContext.Shell);
        content = content.Replace("{{shell.flag}}", PlatformContext.ShellFlag);
        content = content.Replace("{{which}}", PlatformContext.Which);
        content = content.Replace("{{platform.ext}}", PlatformContext.ExecutableExtension);
        content = content.Replace("{{platform.separator}}", PlatformContext.PathSeparator);
        
        return content;
    }
}