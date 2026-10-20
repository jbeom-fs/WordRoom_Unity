using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WordRoom.World
{
    /// <summary>
    /// Rent/return pool of one prefab's instances (one pool for rooms, one for doors). No capacity limit: how
    /// many are shown is decided by the display set, not here. Tracks rented instances so the same instance is
    /// never rented twice or returned twice. Main thread only.
    /// The parent root and the create callback are fixed when the pool is made (at an experiment start); the
    /// callback receives that parent and must not read Inspector fields. A different root means a new pool.
    /// </summary>
    sealed class ViewPool<T> where T : Component
    {
        readonly Func<Transform, T> create;
        readonly Stack<T> free = new Stack<T>();
        readonly HashSet<T> rented = new HashSet<T>();

        /// <summary>Root every instance of this pool is created under (fixed for the pool's life).</summary>
        public Transform Parent { get; }
        public int Created { get; private set; }
        public int Rents { get; private set; }
        public int Returns { get; private set; }
        public int ActiveCount => rented.Count;
        public int FreeCount => free.Count;

        public ViewPool(Transform parent, Func<Transform, T> create)
        {
            Parent = parent;
            this.create = create;
        }

        public T Rent(out bool created)
        {
            T item = null;
            while (item == null && free.Count > 0) item = free.Pop();      // skips instances destroyed from outside
            created = item == null;
            if (created)
            {
                if (Parent == null)                                        // destroyed root: never fall back to another parent
                    throw new InvalidOperationException($"{typeof(T).Name} 풀의 루트가 파괴되어 새 인스턴스를 만들 수 없습니다. 루트를 확인하고 새 실험을 시작하세요.");
                item = create(Parent);
                Created++;
            }
            if (!rented.Add(item)) throw new InvalidOperationException($"{typeof(T).Name} 인스턴스 중복 대여");
            Rents++;
            return item;
        }

        /// <summary>False when the instance is not rented (double return); nothing changes then.</summary>
        public bool Return(T item)
        {
            if (ReferenceEquals(item, null) || !rented.Remove(item)) return false;
            Returns++;
            if (item == null) return true;                                 // destroyed from outside: forget it
            item.gameObject.SetActive(false);
            free.Push(item);
            return true;
        }

        /// <summary>Adds the transforms of every instance this pool made (rented or waiting) to `into`.</summary>
        public void CollectInstances(HashSet<Transform> into)
        {
            foreach (var x in rented)
                if (x != null) into.Add(x.transform);
            foreach (var x in free)
                if (x != null) into.Add(x.transform);
        }

        /// <summary>Destroys only the instances this pool made; their parent root and its other children stay.</summary>
        public void DestroyAll()
        {
            foreach (var x in rented)
                if (x != null) Object.Destroy(x.gameObject);
            foreach (var x in free)
                if (x != null) Object.Destroy(x.gameObject);
            rented.Clear();
            free.Clear();
        }
    }
}
