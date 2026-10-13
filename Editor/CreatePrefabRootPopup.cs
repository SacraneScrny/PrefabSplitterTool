using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PrefabSplitterTool.Editor
{
    /// <summary>
    /// Popup opened from GameObject/Create Prefab Root.
    /// Tree expands as you check nodes; only checked nodes go INTO the prefab,
    /// the rest stays in the scene.
    /// </summary>
    public sealed class CreatePrefabRootPopup : EditorWindow
    {
        sealed class Job
        {
            public GameObject Parent;
            public readonly HashSet<GameObject> Keep = new();
        }

        readonly List<Job> _jobs = new();
        readonly Dictionary<int, bool> _expanded = new();
        Vector2 _scroll;

        public static void Open(List<GameObject> parents)
        {
            if (parents == null || parents.Count == 0) return;
            var w = GetWindow<CreatePrefabRootPopup>(true, "Create Prefab Root", true);
            w.minSize = new Vector2(320, 240);
            w.maxSize = new Vector2(520, 700);
            w._jobs.Clear();
            foreach (var p in parents)
            {
                if (p == null) continue;
                w._jobs.Add(new Job { Parent = p });
            }
        }

        void OnGUI()
        {
            _jobs.RemoveAll(j => j.Parent == null);
            if (_jobs.Count == 0) { Close(); return; }

            EditorGUILayout.HelpBox(
                "Check what goes INTO the prefab.\nUnchecked stays in the scene.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("In prefab:", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("All", EditorStyles.miniButton, GUILayout.Width(45))) SetAllJobs(true);
                if (GUILayout.Button("None", EditorStyles.miniButton, GUILayout.Width(50))) SetAllJobs(false);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var job in _jobs)
            {
                job.Keep.RemoveWhere(c => c == null);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(
                            $"{job.Parent.name}  ({CountKept(job)} in prefab)",
                            EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("All", EditorStyles.miniButton, GUILayout.Width(38)))
                            AddSubtree(job, job.Parent.transform);
                        if (GUILayout.Button("None", EditorStyles.miniButton, GUILayout.Width(44)))
                            job.Keep.Clear();
                    }

                    if (job.Parent.transform.childCount == 0)
                    {
                        EditorGUILayout.LabelField("No children — root only.", EditorStyles.miniLabel);
                    }
                    else
                    {
                        DrawNode(job, job.Parent.transform, 0);
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Cancel")) Close();
                string btn = _jobs.Count == 1 ? "Create Prefab" : $"Create {_jobs.Count} Prefabs";
                if (GUILayout.Button(btn, GUILayout.Height(28)))
                {
                    if (CreatePrefabRoot.CreatePrefabs(ToCoreJobs()))
                        Close();
                }
            }
        }

        void DrawNode(Job job, Transform node, int depth)
        {
            bool isRoot = node.gameObject == job.Parent;
            bool nodeKept = isRoot || job.Keep.Contains(node.gameObject);
            bool hasKids = node.childCount > 0;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * 14f);
                if (hasKids && nodeKept)
                {
                    int id = node.gameObject.GetInstanceID();
                    bool exp = GetExpanded(id);
                    bool nexp = EditorGUILayout.Foldout(exp, "", false);
                    if (nexp != exp) _expanded[id] = nexp;
                }
                else
                {
                    GUILayout.Space(14);
                }

                if (isRoot)
                {
                    EditorGUILayout.LabelField(node.name + "  (root)", EditorStyles.boldLabel);
                }
                else
                {
                    bool nv = EditorGUILayout.ToggleLeft(
                        node.name + (node.gameObject.activeSelf ? "" : "  (inactive)"), nodeKept);
                    if (nv && !nodeKept)
                        job.Keep.Add(node.gameObject);
                    else if (!nv && nodeKept)
                        RemoveSubtree(job, node);
                }
            }

            if (nodeKept && hasKids && GetExpanded(node.gameObject.GetInstanceID()))
                foreach (Transform c in node)
                    DrawNode(job, c, depth + 1);
        }

        bool GetExpanded(int id) => !_expanded.TryGetValue(id, out bool v) || v;

        static void AddSubtree(Job job, Transform node)
        {
            foreach (Transform c in node)
            {
                job.Keep.Add(c.gameObject);
                AddSubtree(job, c);
            }
        }

        static void RemoveSubtree(Job job, Transform node)
        {
            job.Keep.Remove(node.gameObject);
            foreach (Transform c in node)
                RemoveSubtree(job, c);
        }

        void SetAllJobs(bool value)
        {
            foreach (var job in _jobs)
            {
                if (value) AddSubtree(job, job.Parent.transform);
                else job.Keep.Clear();
            }
        }

        int CountKept(Job job)
        {
            int n = 1; // root
            CountWalk(job.Parent.transform, job.Keep, ref n);
            return n;
        }

        static void CountWalk(Transform node, HashSet<GameObject> keep, ref int n)
        {
            foreach (Transform c in node)
            {
                if (!keep.Contains(c.gameObject)) continue;
                n++;
                CountWalk(c, keep, ref n);
            }
        }

        List<(GameObject parent, HashSet<GameObject> keep)> ToCoreJobs()
        {
            var result = new List<(GameObject, HashSet<GameObject>)>();
            foreach (var job in _jobs)
            {
                if (job.Parent == null) continue;
                job.Keep.RemoveWhere(c => c == null);
                result.Add((job.Parent, new HashSet<GameObject>(job.Keep)));
            }
            return result;
        }
    }
}
