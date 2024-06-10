using System.Collections.Generic;
using System.Text.RegularExpressions;

using UnityEditor;
using UnityEngine;

namespace Dirt
{
    public class ModificationReference
    {
        GameObject owner;
        Object target;

        public string targetPath { get; private set; }
        public List<int> targetIndexedPath { get; private set; }
        public string targetPropertyValue { get; private set; }
        public bool hasTargetPropertyObjectValue { get; private set; }
        public Object targetPropertyObjectValue { get; private set; }
        public bool isNothing { get; private set; }
        public bool hasValue { get; private set; }

        public ModificationReference(GameObject owner, Object target)
        {
            SetOwner(owner);
            SetTarget(target);
        }

        public void SetOwner(GameObject owner)
        {
            this.owner = owner;
        }

        public GameObject GetOwner()
        {
            if (!owner)
                Debug.LogWarning($"Reference has no owner");

            return owner;
        }

        public void SetTarget(Object target)
        {
            this.target = target;
        }

        public Object GetTarget()
        {
            if (!target)
                Debug.LogWarning($"{(owner ? $"{owner.name}: " : "")}Reference has no target");

            return target;
        }

        public SerializedProperty GetTargetProperty(Modification modification)
        {
            var target = GetTarget();
            if (!target)
                return null;

            var prefabObject = new SerializedObject(target);
            return prefabObject.FindProperty(modification.propertyPath);
        }

        public int Setup(Modification modification)
        {
            var componentIndex = -1;

            var owner = GetOwner();
            var target = GetTarget();

            if (owner && target)
            {
                var parent = target is Transform transform
                    ? transform
                    : target is GameObject gameObject
                        ? gameObject.transform
                        : (target as Component).transform;

                var components = parent.GetComponents<Component>();
                componentIndex = ArrayUtility.IndexOf(components, target);

                var path = new List<string>();
                targetIndexedPath = new List<int>();

                while (parent && parent != owner.transform)
                {
                    path.Insert(0, parent.name);
                    targetIndexedPath.Insert(0, parent.GetSiblingIndex());
                    parent = parent.parent;
                }

                targetPath = string.Join("/", path);
            }
            else
            {
                targetPath = string.Empty;
            }

            var p = GetTargetProperty(modification);
            string v;

            if (p is null)
            {
                isNothing = true;
                v = "<color=#777777>(nothing)</color>";
            }
            else
            {
                hasValue = true;

                switch (p.type)
                {
                    case "bool":
                        v = $"<color=#9999ff>{p.boolValue}</color>";
                        break;

                    case "short":
                    case "int":
                    case "uint":
                        v = $"<color=#33ff33>{p.intValue}</color>";
                        break;

                    case "float":
                        v = $"<color=#99ff99>{p.floatValue:0.00}</color>";
                        break;

                    case "string":
                        v = p.stringValue == string.Empty
                            ? $"<color=#cc9900>(empty)</color>"
                            : $"<color=#ffcc66>{p.stringValue}</color>";
                        break;

                    case "Enum":
                        if (0 <= p.enumValueIndex && p.enumValueIndex < p.enumNames.Length)
                            v = $"<color=#ffff00>{p.enumNames[p.enumValueIndex]}</color>";
                        else
                            v = $"<color=#ffff00>[type Enum] {p.enumValueIndex}</color>";
                        break;

                    case "ArraySize":
                        v = p.intValue.ToString();
                        break;

                    default:
                        if (Regex.IsMatch(p.type, @"^PPtr\<.+\>$"))
                        {
                            hasTargetPropertyObjectValue = true;
                            targetPropertyObjectValue = p.objectReferenceValue;
                            v = string.Empty;
                            break;
                        }

                        v = $"[type {p.type}]";
                        v = $"<color=#999999>{v}</color>";
                        hasValue = false;
                        break;
                }
            }

            targetPropertyValue = v;

            return componentIndex;
        }
    }
}
