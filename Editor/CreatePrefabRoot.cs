using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PrefabSplitterTool.Editor
{
    public static class CreatePrefabRoot
    {
        internal const string LastFolderKey = "PrefabSplitterTool.LastFolder";
        internal const string DefaultFolder = "Assets/Prefabs";

        [MenuItem("GameObject/Create Prefab Root", false, 30)]
        static void OpenPopup() => CreatePrefabRootPopup.Open(TopLevel(Selection.gameObjects));

        [MenuItem("GameObject/Create Prefab Root", true)]
        static bool Validate() => Selection.activeGameObject != null;

        internal static List<GameObject> TopLevel(GameObject[] selection)
        {
            var result = new List<GameObject>();
            if (selection == null) return result;
            var set = new HashSet<GameObject>(selection);
            foreach (var go in selection)
            {
                if (go == null) continue;
                bool nested = false;
                for (var p = go.transform.parent; p != null; p = p.parent)
                    if (set.Contains(p.gameObject)) { nested = true; break; }
                if (!nested) result.Add(go);
            }
            return result;
        }

        /// <summary>Returns false if the user cancelled the save dialog.</summary>
        internal static bool CreatePrefabs(List<(GameObject parent, HashSet<GameObject> keep)> jobs)
        {
            jobs.RemoveAll(j => j.parent == null);
            if (jobs.Count == 0) return true;

            var created = new List<(GameObject parent, HashSet<GameObject> keep, string path)>();

            if (jobs.Count == 1)
            {
                string folder = EditorPrefs.GetString(LastFolderKey, DefaultFolder);
                if (!AssetDatabase.IsValidFolder(folder)) folder = "Assets";

                string path = EditorUtility.SaveFilePanelInProject(
                    "Create Prefab Root",
                    jobs[0].parent.name,
                    "prefab",
                    "Save root prefab",
                    folder);
                if (string.IsNullOrEmpty(path)) return false; // cancelled

                EditorPrefs.SetString(LastFolderKey, Path.GetDirectoryName(path)?.Replace('\\', '/') ?? folder);
                SavePrefabWithSelection(jobs[0].parent, jobs[0].keep, path);
                created.Add((jobs[0].parent, jobs[0].keep, path));
            }
            else
            {
                string startDir = ToAbsolutePath(EditorPrefs.GetString(LastFolderKey, DefaultFolder));
                string absolute = EditorUtility.OpenFolderPanel("Folder for root prefabs", startDir, "");
                if (string.IsNullOrEmpty(absolute)) return false; // cancelled

                string folder = ToAssetPath(absolute);
                if (string.IsNullOrEmpty(folder))
                {
                    EditorUtility.DisplayDialog("Create Prefab Root", "Folder must be inside Assets.", "OK");
                    return false;
                }

                EditorPrefs.SetString(LastFolderKey, folder);
                EnsureFolderExists(folder);

                try
                {
                    AssetDatabase.StartAssetEditing();
                    foreach (var job in jobs)
                    {
                        string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Sanitize(job.parent.name)}.prefab");
                        SavePrefabWithSelection(job.parent, job.keep, path);
                        created.Add((job.parent, job.keep, path));
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var instances = new List<GameObject>();
            var madePaths = new List<string>();
            foreach (var c in created)
            {
                if (c.parent == null) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(c.path);
                if (prefab != null)
                    instances.Add(ReplaceWithPrefabKeepExtras(c.parent, prefab, c.keep));
                madePaths.Add(c.path);
            }

            AssetDatabase.SaveAssets();
            if (madePaths.Count > 0)
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(madePaths[0]));
            if (instances.Count > 0)
                Selection.objects = instances.ToArray();

            Debug.Log($"[Prefab Root] Created {madePaths.Count} prefab(s): {string.Join(", ", madePaths)}");
            return true;
        }

        /// <summary>
        /// Only the checked nodes (and the root) end up in the prefab.
        /// A checked node counts only if its whole ancestor chain is checked.
        /// </summary>
        internal static void SavePrefabWithSelection(GameObject source, HashSet<GameObject> keep, string path)
        {
            var effective = EffectiveKeep(source, keep);
            var clone = Object.Instantiate(source);
            clone.name = source.name;
            try
            {
                if (PrefabUtility.IsPartOfPrefabInstance(clone))
                    PrefabUtility.UnpackPrefabInstance(clone, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                PruneClone(clone.transform, source.transform, effective);
                ResetTransform(clone);
                PrefabUtility.SaveAsPrefabAsset(clone, path);
            }
            finally
            {
                Object.DestroyImmediate(clone);
            }
        }

        static void PruneClone(Transform cloneNode, Transform origNode, HashSet<GameObject> effective)
        {
            for (int i = cloneNode.childCount - 1; i >= 0; i--)
            {
                var cc = cloneNode.GetChild(i);
                var oc = origNode.GetChild(i);
                if (!effective.Contains(oc.gameObject))
                    Object.DestroyImmediate(cc.gameObject);
                else
                    PruneClone(cc, oc, effective);
            }
        }

        static HashSet<GameObject> EffectiveKeep(GameObject root, HashSet<GameObject> keep)
        {
            var eff = new HashSet<GameObject>();
            foreach (Transform c in root.transform)
                WalkKeep(c, true, keep, eff);
            return eff;
        }

        static void WalkKeep(Transform node, bool parentKept, HashSet<GameObject> keep, HashSet<GameObject> eff)
        {
            bool mine = parentKept && keep.Contains(node.gameObject);
            if (mine) eff.Add(node.gameObject);
            foreach (Transform c in node)
                WalkKeep(c, mine, keep, eff);
        }

        /// <summary>
        /// Replaces the scene object with a prefab instance.
        /// Baked nodes come from the prefab itself (their scene originals are removed);
        /// everything else is reattached under the matching instance node, order preserved.
        /// </summary>
        internal static GameObject ReplaceWithPrefabKeepExtras(GameObject original, GameObject prefabAsset, HashSet<GameObject> keep)
        {
            var effective = EffectiveKeep(original, keep);
            var parent = original.transform.parent;
            int sibling = original.transform.GetSiblingIndex();

            Vector3 pos = original.transform.localPosition;
            Quaternion rot = original.transform.localRotation;
            Vector3 scale = original.transform.localScale;

            bool isRect = original.TryGetComponent<RectTransform>(out var srcRt);
            Vector2 anchoredPos = default, sizeDelta = default, anchorMin = default, anchorMax = default, pivot = default;
            if (isRect)
            {
                anchoredPos = srcRt.anchoredPosition;
                sizeDelta = srcRt.sizeDelta;
                anchorMin = srcRt.anchorMin;
                anchorMax = srcRt.anchorMax;
                pivot = srcRt.pivot;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Create Prefab Root");
            int undoGroup = Undo.GetCurrentGroup();

            Undo.RegisterFullObjectHierarchyUndo(original, "Create Prefab Root");

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);
            Undo.RegisterCreatedObjectUndo(instance, "Create Prefab Root");

            Undo.SetTransformParent(instance.transform, parent, "Create Prefab Root");
            instance.transform.SetSiblingIndex(sibling);
            instance.name = original.name;
            instance.SetActive(original.activeSelf);

            if (instance.TryGetComponent<RectTransform>(out var dstRt) && isRect)
            {
                dstRt.anchorMin = anchorMin;
                dstRt.anchorMax = anchorMax;
                dstRt.pivot = pivot;
                dstRt.sizeDelta = sizeDelta;
                dstRt.anchoredPosition = anchoredPos;
                dstRt.localRotation = rot;
                dstRt.localScale = scale;
            }
            else
            {
                instance.transform.localPosition = pos;
                instance.transform.localRotation = rot;
                instance.transform.localScale = scale;
            }

            RehomePruned(original.transform, instance.transform, effective);
            Undo.DestroyObjectImmediate(original);
            Undo.CollapseUndoOperations(undoGroup);
            return instance;
        }

        static void RehomePruned(Transform origNode, Transform instNode, HashSet<GameObject> effective)
        {
            int origCount = origNode.childCount;
            var origKids = new Transform[origCount];
            for (int i = 0; i < origCount; i++)
                origKids[i] = origNode.GetChild(i);

            // Prefab children mirror the kept scene children in order — grab them upfront,
            // before reparenting shifts sibling indices.
            int keptTotal = 0;
            foreach (var k in origKids)
                if (effective.Contains(k.gameObject)) keptTotal++;

            // Guard: if the prefab structure ever diverges from the scene selection,
            // fall back to keeping everything in the scene instead of throwing mid-undo.
            var eff = effective;
            Transform[] keptMap;
            if (keptTotal == instNode.childCount)
            {
                keptMap = new Transform[keptTotal];
                for (int i = 0; i < keptTotal; i++)
                    keptMap[i] = instNode.GetChild(i);
            }
            else
            {
                Debug.LogWarning("[Prefab Root] Prefab/scene structure mismatch — keeping all children in scene.");
                eff = new HashSet<GameObject>();
                keptMap = new Transform[0];
            }

            int ki = 0, moved = 0;
            foreach (var c in origKids)
            {
                if (eff.Contains(c.gameObject))
                {
                    RehomePruned(c, keptMap[ki++], eff);
                    Undo.DestroyObjectImmediate(c.gameObject);
                }
                else
                {
                    Vector3 lp = c.localPosition;
                    Quaternion lr = c.localRotation;
                    Vector3 ls = c.localScale;
                    bool r = c.TryGetComponent<RectTransform>(out var crt);
                    Vector2 ap = default, sd = default, amn = default, amx = default, pv = default;
                    if (r)
                    {
                        ap = crt.anchoredPosition;
                        sd = crt.sizeDelta;
                        amn = crt.anchorMin;
                        amx = crt.anchorMax;
                        pv = crt.pivot;
                    }

                    Undo.SetTransformParent(c, instNode, "Create Prefab Root");
                    Undo.RecordObject(c.transform, "Create Prefab Root");
                    if (r)
                    {
                        crt.anchorMin = amn;
                        crt.anchorMax = amx;
                        crt.pivot = pv;
                        crt.sizeDelta = sd;
                        crt.anchoredPosition = ap;
                        crt.localRotation = lr;
                        crt.localScale = ls;
                    }
                    else
                    {
                        c.localPosition = lp;
                        c.localRotation = lr;
                        c.localScale = ls;
                    }
                    c.SetSiblingIndex(ki + moved);
                    moved++;
                }
            }
        }

        internal static void ResetTransform(GameObject go)
        {
            if (go.TryGetComponent<RectTransform>(out var rt))
            {
                rt.anchoredPosition = Vector2.zero;
                rt.localRotation = Quaternion.identity;
                rt.localScale = Vector3.one;
            }
            else
            {
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
            }
        }

        internal static string Sanitize(string name)
        {
            name = name.Replace('/', '_').Replace('\\', '_');
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            return string.IsNullOrEmpty(name) ? "Unnamed" : name;
        }

        internal static void EnsureFolderExists(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
                return;
            string[] parts = assetPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        internal static string ToAbsolutePath(string assetPath)
        {
            if (!assetPath.StartsWith("Assets"))
                return Application.dataPath;
            string relative = assetPath.Length > "Assets".Length
                ? assetPath["Assets".Length..].TrimStart('/')
                : "";
            return Path.Combine(Application.dataPath, relative);
        }

        internal static string ToAssetPath(string absolute)
        {
            string dataPath = Application.dataPath.Replace('\\', '/');
            absolute = absolute.Replace('\\', '/');
            if (!absolute.StartsWith(dataPath))
                return null;
            return "Assets" + absolute[dataPath.Length..];
        }
    }
}
