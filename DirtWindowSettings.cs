using System;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;

namespace Dirt
{
    public class DirtWindowSettings : UnityEngine.ScriptableObject
    {
        public bool showExclusions;
        public bool showOnlyUnchangedDirtyValues;

        [Serializable]
        public class Exclusion
        {
            public bool ignore;
            public bool force;
            public GameObject owner;
            public string targetType;
            public bool useTargetPath;
            public string targetPath;
            public string propertyPath;

            public bool Match(DirtWindow.Modification modification)
            {
                if (ignore)
                    return false;

                if (owner)
                    if (owner != modification.prefab.owner)
                        return false;

                if (targetType != string.Empty)
                    if (targetType != modification.prefab.target.GetType().FullName)
                        return false;

                if (useTargetPath)
                    if (modification.prefab.targetPath != targetPath)
                        return false;

                if (propertyPath != string.Empty)
                    if (propertyPath != modification.propertyPath)
                        return false;

                return true;
            }
        }
        public List<Exclusion> exclusions;

        public enum ExclusionState { Included, Excluded, ExcludedButVisible }

        public ExclusionState GetExclusionState(DirtWindow.Modification modification)
        {
            var matches = (exclusions ??= new()).Where(x => x.Match(modification)).ToList();

            if (matches.Count > 0)
            {
                if (showExclusions)
                {
                    if (matches.All(x => x.force))
                        return ExclusionState.Excluded;

                    return ExclusionState.ExcludedButVisible;
                }

                return ExclusionState.Excluded;
            }

            return ExclusionState.Included;
        }
    }
}
