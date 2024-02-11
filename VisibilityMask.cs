using System;

namespace Dirt
{
    [Flags]
    public enum VisibilityMask
    {
        ChangedDirtyValues = 1,
        UnchangedDirtyValues = 2,
        Exclusions = 4
    }
}
