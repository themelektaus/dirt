using System.Linq;

using UnityEditor;
using UnityEngine;

namespace Dirt
{
    public class Modification
    {
        public string propertyPath;

        public ModificationReference instance;
        public ModificationReference prefab;
        public bool excluded;
        bool appliedOrReverted;

        public Modification(GameObject prefabOwner, GameObject instanceOwner, PropertyModification propertyModification)
        {
            propertyPath = propertyModification.propertyPath;

            prefab = new(prefabOwner, propertyModification.target);

            var componentIndex = prefab.Setup(this);

            var settings = DirtWindowSettings.instance;

            excluded = settings.IsExcluded(this);

            if (excluded && !settings.visibility.HasFlag(VisibilityMask.Exclusions))
                return;

            var instanceTransform = instanceOwner.transform;
            instance = new(instanceOwner, instanceTransform);

            var target = instanceTransform;

            if (prefab.targetPath != string.Empty)
                target = target.Find(prefab.targetPath);

            if (!target)
            {
                target = instanceTransform;
                var indexed = prefab.targetIndexedPath.ToList();
                while (target && indexed.Count > 0)
                {
                    var i = indexed[0];
                    target = i < target.childCount ? target.GetChild(i) : null;
                    indexed.RemoveAt(0);
                }
            }

            if (target)
            {

                if (prefab.GetTarget() is GameObject)
                    instance.SetTarget(target.gameObject);
                else if (componentIndex > -1)
                {
                    var components = target.GetComponents<Component>();
                    if (componentIndex < components.Length)
                        instance.SetTarget(components[componentIndex]);
                }
            }

            instance.Setup(this);
        }

        public bool IsUnchangedButDirty()
        {
            if (appliedOrReverted)
                return false;

            if (prefab.isNothing && instance.isNothing)
                return true;

            if (!prefab.hasValue)
                return false;

            if (!instance.hasValue)
                return false;

            if (prefab.targetPropertyValue != instance.targetPropertyValue)
                return false;

            if (prefab.targetPropertyObjectValue != instance.targetPropertyObjectValue)
                return false;

            return true;
        }

        public void Apply()
        {
            appliedOrReverted = true;

            PrefabUtility.ApplyPropertyOverride(
                instance.GetTargetProperty(this),
                AssetDatabase.GetAssetPath(prefab.GetOwner()),
                InteractionMode.UserAction
            );
        }

        public void Revert()
        {
            appliedOrReverted = true;

            var p = instance.GetTargetProperty(this);
            if (p is not null)
            {
                PrefabUtility.RevertPropertyOverride(p, InteractionMode.UserAction);
                return;
            }

            PrefabUtility.RemoveUnusedOverrides(
                new[] { instance.GetOwner() },
                InteractionMode.UserAction
            );
        }
    }

}
