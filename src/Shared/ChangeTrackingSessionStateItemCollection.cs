//
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.
//

using System;
using System.CodeDom;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Web.SessionState;

namespace Microsoft.Web.Redis
{
    /* We can not use SessionStateItemCollection as it is as we need a way to track if item inside session was modified or not in any given request.
       so we use SessionStateItemCollection as backbon for storing session information and keep list of items that are deleted and updated/inserted
       during any request cycle. We use this list to indentify if we want to change any session item or not.*/
    internal class ChangeTrackingSessionStateItemCollection : NameObjectCollectionBase, ISessionStateItemCollection, ICollection, IEnumerable
    {
        private readonly RedisUtility redisUtility;
        SessionStateItemCollection innerCollection;
        // key is "session key in lowercase" and value is "actual session key in actual case"
        Dictionary<string, string> allKeys = new Dictionary<string, string>();
        SerializedItems serializedItems = new SerializedItems();
        object serializedItemsLock = new object();
        HashSet<string> modifiedKeys = new HashSet<string>();
        HashSet<string> deletedKeys = new HashSet<string>();

        internal class SerializedItems : NameObjectCollectionBase
        {
            public void Set(string key, object value)
            {
                BaseSet(key, value);
            }

            public void Clear()
            {
                BaseClear();
            }

            public void Remove(string name)
            {
                BaseRemove(name);
            }

            public void RemoveAt(int index)
            {
                BaseRemoveAt(index);
            }

            public object Get(string name)
            {
                return BaseGet(name);
            }

            public object Get(int index)
            {
                return BaseGet(index);
            }
        }

        private string GetSessionNormalizedKeyToUse(string name)
        { 
            string actualNameStoredEarlier;
            if (allKeys.TryGetValue(name.ToUpperInvariant(), out actualNameStoredEarlier))
            {
                return actualNameStoredEarlier;
            }
            allKeys.Add(name.ToUpperInvariant(), name);
            return name;
        }

        private void addInModifiedKeys(string key)
        {
            Dirty = true;
            if (deletedKeys.Contains(key))
            {
                deletedKeys.Remove(key);
            }
            modifiedKeys.Add(key);
        }

        private void addInDeletedKeys(string key)
        {
            Dirty = true;
            if (modifiedKeys.Contains(key))
            {
                modifiedKeys.Remove(key);
            }
            deletedKeys.Add(key);
        }

        public void AddSerializedData(string name, byte[] value)
        {
            name = GetSessionNormalizedKeyToUse(name);
            // This keeps the order of the two collections equal
            // innerCollection[name] and serializedItems.Set() both call down to BaseSet()
            lock (serializedItemsLock)
            {
                serializedItems.Set(name, value);
                innerCollection[name] = null;
            }
        }
        
        public HashSet<string> GetModifiedKeys()
        {
            return modifiedKeys;
        }
        
        public HashSet<string> GetDeletedKeys()
        {
            return deletedKeys;
        }

        public ChangeTrackingSessionStateItemCollection()
        {
            innerCollection = new SessionStateItemCollection();
        }

        public ChangeTrackingSessionStateItemCollection(RedisUtility redisUtility)
        {
            this.redisUtility = redisUtility;
            innerCollection = new SessionStateItemCollection();
        }

        public void Clear()
        {
            foreach (string key in innerCollection.Keys) 
            {
                addInDeletedKeys(key);
            }
            lock (serializedItemsLock)
            {
                innerCollection.Clear();
                serializedItems.Clear();
            }
        }

        public bool Dirty
        {
            get
            {
                return innerCollection.Dirty;
            }
            set
            {
                innerCollection.Dirty = value;
                if (!value)
                {
                    modifiedKeys.Clear();
                    deletedKeys.Clear();
                }
            }
        }

        public override NameObjectCollectionBase.KeysCollection Keys
        {
            get { return innerCollection.Keys; }
        }

        public void Remove(string name)
        {
            name = GetSessionNormalizedKeyToUse(name);
            if (innerCollection[name] != null)
            {
                addInDeletedKeys(name);
            }
            lock (serializedItemsLock)
            {
                innerCollection.Remove(name);
                serializedItems.Remove(name);
            }
        }

        public void RemoveAt(int index)
        {
            if (innerCollection.Keys[index] != null)
            {
                addInDeletedKeys(innerCollection.Keys[index]);
            }
            lock (serializedItemsLock)
            {
                innerCollection.RemoveAt(index);
                if (serializedItems.Count > index) serializedItems.RemoveAt(index);
            }
        }

        private object internalGet(int index)
        {
            var obj = innerCollection[index];
            if (obj != null || redisUtility == null) return obj;
            object serializedItem;
            lock (serializedItemsLock)
            {
                serializedItem = serializedItems.Get(index);
            }
            if (serializedItem == null) return null;
            innerCollection[index] = obj = redisUtility.GetObjectFromBytes((byte[])serializedItem);
            return obj;
        }

        private object internalGet(string name)
        {
            var obj = innerCollection[name];
            if (obj != null || redisUtility == null) return obj;
            object serializedItem;
            lock (serializedItemsLock)
            {
                serializedItem = serializedItems.Get(name);
            }
            if (serializedItem == null) return null;
            innerCollection[name] = obj = redisUtility.GetObjectFromBytes((byte[])serializedItem);
            return obj;
        }

        public object this[int index]
        {
            get
            {
                var item = internalGet(index);
                if (IsMutable(item))
                {
                    addInModifiedKeys(innerCollection.Keys[index]);
                }
                return item;
            }
            set
            {
                addInModifiedKeys(innerCollection.Keys[index]);
                innerCollection[index] = value;
            }
        }

        public object this[string name]
        {
            get
            {
                name = GetSessionNormalizedKeyToUse(name);
                var item = internalGet(name);
                if (IsMutable(item))
                {
                    addInModifiedKeys(name);
                }
                return item;
            }
            set
            {
                name = GetSessionNormalizedKeyToUse(name);
                addInModifiedKeys(name);
                innerCollection[name] = value;
            }
        }

        private bool IsMutable(object data)
        {
            if (data != null && !data.GetType().IsValueType && data.GetType() != typeof(string))
            {
                return true;
            }
            return false;
        }

        public override IEnumerator GetEnumerator()
        {
            return innerCollection.GetEnumerator();
        }

        public override int Count
        {
            get { return innerCollection.Count; }
        }
    }
}
