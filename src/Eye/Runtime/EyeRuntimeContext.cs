namespace StealthEye.Runtime;

public enum EyeRuntimeMode
{
    Cli,
    Service,
    Session,
}

public sealed record EyeRuntimeContext(EyeRuntimeMode Mode)
{
    public bool IsService => Mode == EyeRuntimeMode.Service;
    public bool IsSession => Mode == EyeRuntimeMode.Session;
    public string ProcessHandlePrefix => Mode switch
    {
        EyeRuntimeMode.Service => "proc_s_",
        EyeRuntimeMode.Session => "proc_u_",
        _ => "proc_c_",
    };
}
