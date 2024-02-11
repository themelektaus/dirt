using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using UnityEditor;
using UnityEngine;

namespace Dirt
{
    public class DirtWindowSettings : ScriptableObject
    {
        public static DirtWindowSettings instance { get; private set; }
        public static void Load([CallerFilePath] string path = null)
        {
            path = Path.GetRelativePath(
                Environment.CurrentDirectory,
                new FileInfo(path).Directory.FullName
            );
            path = Path.Combine(path, $"{nameof(DirtWindowSettings)}.asset");

            instance = AssetDatabase.LoadAssetAtPath<DirtWindowSettings>(path);

            if (instance)
                return;

            instance = CreateInstance<DirtWindowSettings>();
            AssetDatabase.CreateAsset(instance, path);
            AssetDatabase.Refresh();
        }

        public Vector2Int modificationLimit = new(3, 10);

        public VisibilityMask visibility
            = VisibilityMask.ChangedDirtyValues
            | VisibilityMask.UnchangedDirtyValues;

        [Serializable]
        public class Exclusion
        {
            public GameObject owner;
            public string targetType;
            public bool useTargetPath;
            public string targetPath;
            public string propertyPath;

            public bool Match(Modification modification)
            {
                if (owner)
                {
                    var prefabOwner = modification.prefab.GetOwner();
                    if (prefabOwner && prefabOwner != owner)
                        return false;
                }

                if (targetType != string.Empty)
                {
                    var prefabTarget = modification.prefab.GetTarget();
                    if (prefabTarget)
                    {
                        var prefabType = prefabTarget.GetType().FullName;
                        if (prefabType != targetType)
                            return false;
                    }
                }

                if (useTargetPath)
                {
                    var prefabTargetPath = modification.prefab.targetPath;
                    if (prefabTargetPath != targetPath)
                        return false;
                }

                if (propertyPath != string.Empty)
                {
                    if (modification.propertyPath != propertyPath)
                        return false;
                }

                return true;
            }
        }
        [SerializeField] List<Exclusion> exclusions = new();

        public void ToggleExclusion(Modification modification)
        {
            if (modification.excluded)
            {
                modification.excluded = false;
                exclusions.RemoveAll(x => x.Match(modification));
                return;
            }

            var target = modification.prefab.GetTarget();

            modification.excluded = true;
            exclusions.Add(new()
            {
                owner = modification.prefab.GetOwner(),
                targetType = target ? target.GetType().FullName : string.Empty,
                useTargetPath = true,
                targetPath = modification.prefab.targetPath,
                propertyPath = modification.propertyPath
            });
        }

        public bool IsExcluded(Modification modification)
        {
            return exclusions.Any(x => x.Match(modification));
        }
    }
}
