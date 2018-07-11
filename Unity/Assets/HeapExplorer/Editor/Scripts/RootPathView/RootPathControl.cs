using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor.IMGUI.Controls;
using UnityEditor;
using System.Linq;

namespace HeapExplorer
{
    public class ObjectProxy
    {
        public PackedMemorySnapshot snapshot;
        public RichNativeObject native;
        public RichManagedObject managed;
        public RichGCHandle gcHandle;
        public RichStaticField staticField;

        public System.Int64 id
        {
            get
            {
                if (native.isValid)
                    return (1 << 62) + native.packed.nativeObjectsArrayIndex;

                if (managed.isValid)
                    return (1 << 61) + managed.packed.managedObjectsArrayIndex;

                if (gcHandle.isValid)
                    return (1 << 60) + gcHandle.packed.gcHandlesArrayIndex;

                if (staticField.isValid)
                    return (1 << 59) + staticField.packed.staticFieldsArrayIndex;

                return 0;
            }
        }

        public ObjectProxy(PackedMemorySnapshot snp, PackedNativeUnityEngineObject packed)
        {
            snapshot = snp;
            native = new RichNativeObject(snp, packed.nativeObjectsArrayIndex);
        }

        public ObjectProxy(PackedMemorySnapshot snp, PackedManagedObject packed)
        {
            snapshot = snp;
            managed = new RichManagedObject(snp, packed.managedObjectsArrayIndex);
        }

        public ObjectProxy(PackedMemorySnapshot snp, PackedGCHandle packed)
        {
            snapshot = snp;
            gcHandle = new RichGCHandle(snp, packed.gcHandlesArrayIndex);
        }

        public ObjectProxy(PackedMemorySnapshot snp, PackedManagedStaticField packed)
        {
            snapshot = snp;
            staticField = new RichStaticField(snp, packed.staticFieldsArrayIndex);
        }

        public override string ToString()
        {
            if (native.isValid)
                return string.Format("Native, {0}", native);

            if (managed.isValid)
                return string.Format("Managed, {0}", managed);

            if (gcHandle.isValid)
                return string.Format("GCHandle, {0}", gcHandle);

            if (staticField.isValid)
                return string.Format("StaticField, {0}", staticField);

            return base.ToString();
        }
    }

    public enum RootPathReason
    {
        // The order of elements here reflects how rootpaths are
        // sorted in the RootPath view. Things that keep objects alive
        // are at the bottom of this enum.

        None = 0,
        AssetBundle,
        Component,
        GameObject,
        UnityManager,
        DontUnloadUnusedAsset,
        DontDestroyOnLoad,
        Static,
        Unknown, // make most important, so I easily spot if I forgot to support something
    }

    public class RootPath : System.IComparable<RootPath>
    {
        public int count
        {
            get
            {
                return m_items.Length;
            }
        }

        public RootPathReason reason
        {
            get;
            private set;
        }
        
        public string reasonString
        {
            get
            {
                switch(reason)
                {
                    case RootPathReason.None:
                        return "";
                    case RootPathReason.AssetBundle:
                        return "this object is an assetbundle, which is never unloaded automatically, but only through an explicit .Unload() call.";
                    case RootPathReason.Component:
                        return "this object is a component, which lives on a gameobject. it will be unloaded on next scene load.";
                    case RootPathReason.GameObject:
                        return "this is a gameobject, that is either part of the loaded scene, or was generated by script. It will be unloaded on next scene load if nobody is referencing it";
                    case RootPathReason.UnityManager:
                        return "this is an internal unity'manager' style object, which is a global object that will never be unloaded";
                    case RootPathReason.DontUnloadUnusedAsset:
                        return "the DontUnloadUnusedAsset hideflag is set on this object. Unity's builtin resources set this flag. Users can also set the flag themselves";
                    case RootPathReason.DontDestroyOnLoad:
                        return "DontDestroyOnLoad() was called on this object, so it will never be unloaded";
                    case RootPathReason.Static:
                        return "Static fields are global variables. Anything they reference will not be unloaded.";
                    case RootPathReason.Unknown:
                        return "This object is a root, but the memory profiler UI does not yet understand why";
                }

                return "???";
            }
        }

        ObjectProxy[] m_items = new ObjectProxy[0];

        public RootPath(RootPathReason reason, ObjectProxy[] path)
            : base()
        {
            this.reason = reason;
            this.m_items = path;
        }

        public ObjectProxy this[int index]
        {
            get
            {
                return m_items[index];
            }
        }

        public int CompareTo(RootPath other)
        {
            var x = ((long)reason << 40) + Mathf.Max(0, int.MaxValue - count);
            var y = ((long)other.reason << 40) + Mathf.Max(0, int.MaxValue - other.count);
            return y.CompareTo(x);
        }
    }

    public class RootPathUtility
    {
        List<RootPath> m_items = new List<RootPath>();

        public int count
        {
            get
            {
                return m_items.Count;
            }
        }

        public RootPath this[int index]
        {
            get
            {
                return m_items[index];
            }
        }

        public RootPath shortestPath
        {
            get
            {
                RootPath value = null;

                if (m_items.Count > 0)
                {
                    value = m_items[0];

                    // Find the shortest path
                    foreach (var p in m_items)
                    {
                        if (p.count < value.count && value.reason != RootPathReason.Static)
                            value = p;

                        // Assign if it's a path to static
                        if (p.reason == RootPathReason.Static && value.reason != RootPathReason.Static)
                            value = p;

                        // Find the shortest path to static
                        if (p.reason == RootPathReason.Static && p.count < value.count)
                            value = p;
                    }
                }

                if (value == null)
                    value = new RootPath(RootPathReason.None, new ObjectProxy[0]);

                return value;
            }
        }

        public void Find(ObjectProxy obj)
        {
            m_items = new List<RootPath>();
            var seen = new HashSet<long>();

            var queue = new Queue<List<ObjectProxy>>();
            queue.Enqueue(new List<ObjectProxy> { obj });

            int guard = 0;
            while (queue.Any())
            {
                if (++guard > 100000)
                {
                    Debug.LogWarning("guard kicked in");
                    m_items = new List<RootPath>();
                    break;
                }

                var pop = queue.Dequeue();
                var tip = pop.Last();

                RootPathReason reason;
                if (IsRoot(tip, out reason))
                {
                    m_items.Add(new RootPath(reason, pop.ToArray()));
                    continue;
                }

                var referencedBy = GetReferencedBy(tip);
                foreach (var next in referencedBy)
                {
                    if (seen.Contains(next.id))
                        continue;
                    seen.Add(next.id);

                    var dupe = new List<ObjectProxy>(pop) { next };
                    queue.Enqueue(dupe);
                }
            }

            m_items.Sort();
        }

        List<ObjectProxy> GetReferencedBy(ObjectProxy obj)
        {
            var referencedBy = new List<PackedConnection>(32);

            if (obj.staticField.isValid)
                obj.snapshot.GetConnections(obj.staticField.packed, null, referencedBy);

            if (obj.native.isValid)
                obj.snapshot.GetConnections(obj.native.packed, null, referencedBy);

            if (obj.managed.isValid)
                obj.snapshot.GetConnections(obj.managed.packed, null, referencedBy);

            if (obj.gcHandle.isValid)
                obj.snapshot.GetConnections(obj.gcHandle.packed, null, referencedBy);

            var value = new List<ObjectProxy>(referencedBy.Count);
            foreach (var c in referencedBy)
            {
                switch (c.fromKind)
                {
                    case PackedConnection.Kind.Native:
                        value.Add(new ObjectProxy(obj.snapshot, obj.snapshot.nativeObjects[c.from]));
                        break;

                    case PackedConnection.Kind.Managed:
                        value.Add(new ObjectProxy(obj.snapshot, obj.snapshot.managedObjects[c.from]));
                        break;

                    case PackedConnection.Kind.GCHandle:
                        value.Add(new ObjectProxy(obj.snapshot, obj.snapshot.gcHandles[c.from]));
                        break;

                    case PackedConnection.Kind.StaticField:
                        value.Add(new ObjectProxy(obj.snapshot, obj.snapshot.managedStaticFields[c.from]));
                        break;
                }
            }
            return value;
        }


        bool IsRoot(ObjectProxy thing, out RootPathReason reason)
        {
            reason = RootPathReason.None;

            if (thing.staticField.isValid)
            {
                reason = RootPathReason.Static;
                return true;
            }

            if (thing.managed.isValid)
            {
                return false;
            }

            if (thing.gcHandle.isValid)
            {
                return false;
            }

            if (!thing.native.isValid)
                throw new System.ArgumentException("Unknown type: " + thing.GetType());

            if (thing.native.isManager)
            {
                reason = RootPathReason.UnityManager;
                return true;
            }

            if (thing.native.isDontDestroyOnLoad)
            {
                reason = RootPathReason.DontDestroyOnLoad;
                return true;
            }

            if ((thing.native.hideFlags & HideFlags.DontUnloadUnusedAsset) != 0)
            {
                reason = RootPathReason.DontUnloadUnusedAsset;
                return true;
            }

            if (thing.native.isPersistent)
            {
                return false;
            }

            if (thing.native.type.IsSubclassOf(thing.snapshot.coreTypes.nativeComponent))
            {
                reason = RootPathReason.Component;
                return true;
            }

            if (thing.native.type.IsSubclassOf(thing.snapshot.coreTypes.nativeGameObject))
            {
                reason = RootPathReason.GameObject;
                return true;
            }

            if (thing.native.type.IsSubclassOf(thing.snapshot.coreTypes.nativeAssetBundle))
            {
                reason = RootPathReason.AssetBundle;
                return true;
            }

            reason = RootPathReason.Unknown;
            return true;
        }
    }

    public class RootPathControl : AbstractTreeView
    {
        //public System.Action<GotoCommand> gotoCB;
        public System.Action<RootPath> onSelectionChange;

        PackedMemorySnapshot m_snapshot;
        int m_uniqueId = 1;

        enum Column
        {
            Type,
            Name,
            Depth,
            Address,
        }

        public RootPathControl(HeapExplorerWindow window, string editorPrefsKey, TreeViewState state)
            : base(window, editorPrefsKey, state, new MultiColumnHeader(
                new MultiColumnHeaderState(new[]
                {
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Type"), width = 200, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("C++ Name"), width = 200, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Depth"), width = 80, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Address"), width = 200, autoResize = true },
                })))
        {
            columnIndexForTreeFoldouts = 0;
            multiColumnHeader.canSort = false;

            Reload();
        }

        protected override void SelectionChanged(IList<int> selectedIds)
        {
            base.SelectionChanged(selectedIds);
            if (onSelectionChange == null)
                return;

            if (selectedIds == null || selectedIds.Count == 0)
            {
                onSelectionChange.Invoke(null);
                return;
            }

            var selectedItem = FindItem(selectedIds[0], rootItem) as Item;
            if (selectedItem == null)
            {
                onSelectionChange.Invoke(null);
                return;
            }

            onSelectionChange.Invoke(selectedItem.rootPath);
        }

        protected override int OnSortItem(TreeViewItem x, TreeViewItem y)
        {
            return 0;
        }

        public TreeViewItem BuildTree(PackedMemorySnapshot snapshot, RootPathUtility paths)
        {
            m_snapshot = snapshot;
            m_uniqueId = 1;

            var root = new TreeViewItem { id = 0, depth = -1, displayName = "Root" };
            if (m_snapshot == null || paths == null || paths.count == 0)
            {
                root.AddChild(new TreeViewItem { id = 1, depth = -1, displayName = "" });
                return root;
            }

            for (var j=0; j< paths.count; ++j)
            {
                var path = paths[j];
                var parent = root;

                for (var n = path.count - 1; n >= 0; --n)
                {
                    var obj = path[n];
                    Item newItem = null;

                    if (obj.native.isValid)
                        newItem = AddNativeUnityObject(parent, obj.native.packed);
                    else if (obj.managed.isValid)
                        newItem = AddManagedObject(parent, obj.managed.packed);
                    else if (obj.gcHandle.isValid)
                        newItem = AddGCHandle(parent, obj.gcHandle.packed);
                    else if (obj.staticField.isValid)
                        newItem = AddStaticField(parent, obj.staticField.packed);

                    if (parent == root)
                    {
                        parent = newItem;
                        newItem.rootPath = path;
                    }
                }
            }

            return root;
        }

        Item AddGCHandle(TreeViewItem parent, PackedGCHandle gcHandle)
        {
            var item = new GCHandleItem
            {
                id = m_uniqueId++,
                depth = parent.depth + 1,
            };

            item.Initialize(this, m_snapshot, gcHandle.gcHandlesArrayIndex);
            parent.AddChild(item);
            return item;
        }

        Item AddManagedObject(TreeViewItem parent, PackedManagedObject managedObject)
        {
            var item = new ManagedObjectItem
            {
                id = m_uniqueId++,
                depth = parent.depth + 1,
            };

            item.Initialize(this, m_snapshot, managedObject.managedObjectsArrayIndex);
            parent.AddChild(item);
            return item;
        }

        Item AddNativeUnityObject(TreeViewItem parent, PackedNativeUnityEngineObject nativeObject)
        {
            var item = new NativeObjectItem
            {
                id = m_uniqueId++,
                depth = parent.depth + 1,
            };

            item.Initialize(this, m_snapshot, nativeObject);
            parent.AddChild(item);
            return item;
        }

        Item AddStaticField(TreeViewItem parent, PackedManagedStaticField staticField)
        {
            var item = new ManagedStaticFieldItem
            {
                id = m_uniqueId++,
                depth = parent.depth + 1,
            };

            item.Initialize(this, m_snapshot, staticField.staticFieldsArrayIndex);
            parent.AddChild(item);
            return item;
        }


        ///////////////////////////////////////////////////////////////////////////
        // TreeViewItem's
        ///////////////////////////////////////////////////////////////////////////

        class Item : AbstractTreeViewItem
        {
            public RootPath rootPath;

            protected RootPathControl m_owner;
            protected string m_value;
            protected System.UInt64 m_address;

            public override void GetItemSearchString(string[] target, out int count)
            {
                count = 0;
                target[count++] = displayName;
                target[count++] = m_value;
                target[count++] = string.Format(StringFormat.Address, m_address);
            }
            
            public override void OnGUI(Rect position, int column)
            {
                if (column == 0 && depth == 0)
                {
                    switch(rootPath.reason)
                    {
                        case RootPathReason.Static:
                        case RootPathReason.DontDestroyOnLoad:
                        case RootPathReason.DontUnloadUnusedAsset:
                        case RootPathReason.UnityManager:
                            GUI.Box(HeEditorGUI.SpaceL(ref position, position.height), new GUIContent(HeEditorStyles.warnImage, rootPath.reasonString), HeEditorStyles.iconStyle);
                            break;
                    }
                }

                switch ((Column)column)
                {
                    case Column.Type:
                        HeEditorGUI.TypeName(position, displayName);
                        break;

                    case Column.Name:
                        EditorGUI.LabelField(position, m_value);
                        break;

                    case Column.Address:
                        if (m_address != 0) // statics dont have an address in PackedMemorySnapshot and I don't want to display a misleading 0
                            HeEditorGUI.Address(position, m_address);
                        break;

                    case Column.Depth:
                        if (rootPath != null)
                            EditorGUI.LabelField(position, rootPath.count.ToString());
                        break;
                }
            }
        }

        // ------------------------------------------------------------------------

        class GCHandleItem : Item
        {
            PackedMemorySnapshot m_snapshot;
            RichGCHandle m_gcHandle;

            public void Initialize(RootPathControl owner, PackedMemorySnapshot snapshot, int gcHandleArrayIndex)
            {
                m_owner = owner;
                m_snapshot = snapshot;
                m_gcHandle = new RichGCHandle(m_snapshot, gcHandleArrayIndex);

                displayName = "GCHandle";
                m_value = m_gcHandle.managedObject.isValid ? m_gcHandle.managedObject.type.name : "";
                m_address = m_gcHandle.managedObjectAddress;
            }

            public override void OnGUI(Rect position, int column)
            {
                if (column == 0)
                {
                    if (HeEditorGUI.GCHandleButton(HeEditorGUI.SpaceL(ref position, position.height)))
                    {
                        m_owner.m_Window.OnGoto(new GotoCommand(m_gcHandle));
                    }

                    if (m_gcHandle.nativeObject.isValid)
                    {
                        if (HeEditorGUI.CppButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_owner.m_Window.OnGoto(new GotoCommand(m_gcHandle.nativeObject));
                        }
                    }

                    if (m_gcHandle.managedObject.isValid)
                    {
                        if (HeEditorGUI.CsButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_owner.m_Window.OnGoto(new GotoCommand(m_gcHandle.managedObject));
                        }
                    }
                }

                base.OnGUI(position, column);
            }
        }

        // ------------------------------------------------------------------------

        class ManagedObjectItem : Item
        {
            RichManagedObject m_managedObject;

            public void Initialize(RootPathControl owner, PackedMemorySnapshot snapshot, int arrayIndex)
            {
                m_owner = owner;
                m_managedObject = new RichManagedObject(snapshot, arrayIndex);

                displayName = m_managedObject.type.name;
                m_address = m_managedObject.address;
                m_value = m_managedObject.nativeObject.isValid ? m_managedObject.nativeObject.name : "";
            }

            public override void OnGUI(Rect position, int column)
            {
                if (column == 0)
                {
                    if (HeEditorGUI.CsButton(HeEditorGUI.SpaceL(ref position, position.height)))
                    {
                        m_owner.m_Window.OnGoto(new GotoCommand(m_managedObject));
                    }

                    if (m_managedObject.gcHandle.isValid)
                    {
                        if (HeEditorGUI.GCHandleButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_owner.m_Window.OnGoto(new GotoCommand(m_managedObject.gcHandle));
                        }
                    }

                    if (m_managedObject.nativeObject.isValid)
                    {
                        if (HeEditorGUI.CppButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_owner.m_Window.OnGoto(new GotoCommand(m_managedObject.nativeObject));
                        }
                    }
                }

                base.OnGUI(position, column);
            }
        }

        // ------------------------------------------------------------------------

        class ManagedStaticFieldItem : Item
        {
            PackedMemorySnapshot m_snapshot;
            PackedManagedStaticField m_staticField;

            public void Initialize(RootPathControl owner, PackedMemorySnapshot snapshot, int arrayIndex)
            {
                m_owner = owner;
                m_snapshot = snapshot;
                m_staticField = m_snapshot.managedStaticFields[arrayIndex];

                var staticClassType = m_snapshot.managedTypes[m_staticField.managedTypesArrayIndex];
                var staticField = staticClassType.fields[m_staticField.fieldIndex];
                //var staticFieldType = m_snapshot.managedTypes[staticField.managedTypesArrayIndex];

                m_address = 0;
                displayName = staticClassType.name;
                m_value = "static " + staticField.name;
            }

            public override void OnGUI(Rect position, int column)
            {
                if (column == 0)
                {
                    if (HeEditorGUI.CsStaticButton(HeEditorGUI.SpaceL(ref position, position.height)))
                    {
                        m_owner.m_Window.OnGoto(new GotoCommand(new RichStaticField(m_snapshot, m_staticField.staticFieldsArrayIndex)));
                    }
                }

                base.OnGUI(position, column);
            }
        }

        // ------------------------------------------------------------------------

        class NativeObjectItem : Item
        {
            PackedMemorySnapshot m_snapshot;
            RichNativeObject m_nativeObject;

            public void Initialize(RootPathControl owner, PackedMemorySnapshot snapshot, PackedNativeUnityEngineObject nativeObject)
            {
                m_owner = owner;
                m_snapshot = snapshot;
                m_nativeObject = new RichNativeObject(snapshot, nativeObject.nativeObjectsArrayIndex);

                m_value = m_nativeObject.name;
                m_address = m_nativeObject.address;
                displayName = m_nativeObject.type.name;

                // If it's a MonoBehaviour or ScriptableObject, use the C# typename instead
                // It makes it easier to understand what it is, otherwise everything displays 'MonoBehaviour' only.
                // TODO: Move to separate method
                if (m_nativeObject.type.IsSubclassOf(m_snapshot.coreTypes.nativeMonoBehaviour) || m_nativeObject.type.IsSubclassOf(m_snapshot.coreTypes.nativeScriptableObject))
                {
                    string monoScriptName;
                    if (m_snapshot.FindNativeMonoScriptType(m_nativeObject.packed.nativeObjectsArrayIndex, out monoScriptName) != -1)
                    {
                        if (!string.IsNullOrEmpty(monoScriptName))
                            displayName = monoScriptName;
                    }
                }
            }

            public override void OnGUI(Rect position, int column)
            {
                if (column == 0)
                {
                    if (HeEditorGUI.CppButton(HeEditorGUI.SpaceL(ref position, position.height)))
                    {
                        m_owner.m_Window.OnGoto(new GotoCommand(m_nativeObject));
                    }

                    if (m_nativeObject.gcHandle.isValid)
                    {
                        if (HeEditorGUI.GCHandleButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_owner.m_Window.OnGoto(new GotoCommand(m_nativeObject.gcHandle));
                        }
                    }

                    if (m_nativeObject.managedObject.isValid)
                    {
                        if (HeEditorGUI.CsButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_owner.m_Window.OnGoto(new GotoCommand(m_nativeObject.managedObject));
                        }
                    }
                }

                base.OnGUI(position, column);
            }
        }

#if false
        

         static void PathLabel12(Rect position, string text)
        {
            // TODO: not happy with it yet, because it generates garbage
            var orgtext = text;
            

            var trycount = 0;
            TryAgain:
            trycount++;
            if (trycount < 5 && EditorStyles.label.CalcSize(new GUIContent(text)).x > position.width)
            {
                text = text.Replace("###", ".");

                var lastdot = text.LastIndexOf('.');
                if (lastdot != -1)
                {
                    var index = text.LastIndexOf('.', lastdot - 1);
                    if (index != -1)
                    {
                        var name = text.Substring(lastdot + 1);
                        var path = text.Substring(0, index);
                        text = path + "###" + name;

                        goto TryAgain;
                    }
                }

                text = "###" + GetFileName(orgtext);
                if (EditorStyles.label.CalcSize(new GUIContent(text)).x > position.width)
                {
                    var dotrect = position;
                    dotrect.x += dotrect.width - 18;
                    dotrect.width = 16;

                    position.width -= dotrect.width;
                    GUI.Label(position, new GUIContent(text.Replace("###", "..."), orgtext));
                    GUI.Label(dotrect, new GUIContent("...", orgtext));
                }
                else
                    GUI.Label(position, new GUIContent(text.Replace("###", "...")));
            }
            else
                GUI.Label(position, new GUIContent(text.Replace("###", "..."), trycount > 1 ? orgtext : ""));
        }

        static string GetFileName(string assetPath)
        {
            // this method is pretty much just System.IO.Path.GetFileName,
            // but it won't thow an exception if an invalid path character is used.
            if (assetPath != null)
            {
                int length = assetPath.Length;
                int num2 = length;
                while (--num2 >= 0)
                {
                    char ch = assetPath[num2];
                    if (ch == '.')
                    {
                        return assetPath.Substring(num2 + 1, (length - num2) - 1);
                    }
                }
            }
            return assetPath;

        }
#endif
    }
}
