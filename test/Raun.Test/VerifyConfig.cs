using System.Runtime.CompilerServices;

namespace Raun.Test;

public static class VerifyConfig
{
    [ModuleInitializer]
    public static void Initialize() => Environment.SetEnvironmentVariable("DiffEngine_Disabled", "true");
}
