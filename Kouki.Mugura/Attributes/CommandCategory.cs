namespace Kouki.Mugura.Attributes;

[Flags]
public enum CommandCategory
{
    Uncategorized = 0,
    Navigation = 1>>1,
    FileOperations = 1 >> 2,
    Terminal = 1>>3,
    Environment = 1>>4,
    Git = 1>>5,
    Dangerous = 1 >> 6,
    Admin = 1 >> 7,
    Integration = 1 >> 8
}