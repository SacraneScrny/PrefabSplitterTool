# Prefab Root

Right-click a `GameObject` → **Create Prefab Root**, check which children belong in
the prefab, and the scene object is swapped for the new prefab instance while
everything else stays where it was. No manual unparenting.

- **Tree picker**: checking a node expands its children; only checked nodes are baked in.
- **Scene stays intact**: unchecked objects are reattached under the matching instance node.
- **Multi-selection**, batch save into one folder, last location remembered.
- **Single undo step** per prefab. Root transform is zeroed in the prefab
  (`RectTransform` anchors/size kept).

## Install

Package Manager → `Add package from git URL…`:

```text
https://github.com/SacraneScrny/com.sackrany.prefabroot.git
```

Or copy this folder into `Packages/`. Unity 2021.3+, editor only, no dependencies.

## Rules

- The selected object is always the prefab root.
- A node is baked in only if its whole ancestor chain is checked.
- Unchecking a node unchecks its subtree.

## License

[MIT](LICENSE.md)
