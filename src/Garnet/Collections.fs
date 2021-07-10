﻿module internal Garnet.Collections

open System
open System.Collections
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Threading
open Garnet.Comparisons

/// Mutable min-heap
type Heap<'k, 'a when 'k :> IComparable<'k>>() =
    // create a dummy value for easier indexing
    let items = List<KeyValuePair<'k, 'a>>()
    do items.Add(Unchecked.defaultof<_>)
    let compare a b = 
        items.[a].Key.CompareTo(items.[b].Key)
    let swap a b =
        let temp = items.[b]
        items.[b] <- items.[a]
        items.[a] <- temp
    let getMinChildIndex parentIndex =
        let ci = parentIndex * 2
        if ci >= items.Count then -1
        else
            // if we have a second child that's smaller, pick it
            // we know that if second exists, first exists due to shape
            let offset =
                if ci + 1 < items.Count && 
                    compare (ci + 1) ci < 0
                    then 1 else 0
            ci + offset
    let rec siftDown index =
        // start at top and swap down through min child
        let ci = getMinChildIndex index
        if ci >= 0 && compare index ci > 0 then
            swap index ci
            siftDown ci
    let rec siftUp index =
        // start at end and swap up through parent
        // maintain parent/child invariant at each iteration
        if index > 1 && compare index (index / 2) < 0 then
            swap index (index / 2)
            siftUp (index / 2)
    member h.Items = items
    member h.Count = items.Count - 1
    member h.Top = items.[1]
    member h.Insert(key, value) =
        items.Add(KeyValuePair(key, value))
        siftUp (items.Count - 1)
    member h.RemoveMin() =
        if h.Count = 0 then failwith "Heap is empty"
        let top = h.Top
        items.[1] <- items.[items.Count - 1]
        items.RemoveAt(items.Count - 1)
        siftDown 1
        top
    member h.Clear() =
        while items.Count > 1 do items.RemoveAt(items.Count - 1)

/// Mutable, min queue (min priority value dequeued first)
type PriorityQueue<'k, 'a when 'k :> IComparable<'k>>() =
    let heap = Heap<'k, 'a>()
    member q.Items = heap.Items
    member q.Count = heap.Count
    member q.Top = heap.Top
    member q.Enqueue(priority, value) =
        heap.Insert(priority, value)
    member q.Dequeue() =
        heap.RemoveMin().Value
    member q.Clear() = 
        heap.Clear()

[<AutoOpen>]
module internal DictionarySlim =    
    [<Struct>]
    type Entry<'TKey, 'TValue 
        when 'TKey :> IEquatable<'TKey>
        and 'TKey : equality> = {
        mutable key : 'TKey
        mutable value : 'TValue
        mutable next : int
    }

    let sizeOneIntArray = Array.zeroCreate<int> 1
    let inline eq<'a when 'a :> System.IEquatable<'a>> (x:'a) (y:'a) = x.Equals y    
    
    let inline hash x mask =
        let h = x.GetHashCode()
        h &&& mask

// Adapted from:
// https://github.com/dotnet/corefxlab/blob/master/src/Microsoft.Experimental.Collections/Microsoft/Collections/Extensions/DictionarySlim.cs
type DictionarySlim<'TKey, 'TValue 
    when 'TKey :> IEquatable<'TKey>
    and 'TKey : equality>(capacity) =
    let mutable _count = 0
    let mutable _freeList = -1
    let mutable _buckets = Array.zeroCreate<int> (max 2 capacity)
    let mutable _entries = Array.zeroCreate<Entry<'TKey, 'TValue>> (max 2 capacity)
    new() = DictionarySlim(2)
    member internal c.Entries = _entries
    member c.Count = _count
    member c.Clear() =
        _count <- 0
        _freeList <- -1
        // changed from original: keeping buffers to avoid GC
        Array.Clear(_buckets, 0, _buckets.Length)
        Array.Clear(_entries, 0, _entries.Length)
    member c.TryGetValue(key : 'TKey, [<Out>] value : byref<'TValue>) =
        let entries = _entries
        let mutable result = false
        let mutable i = _buckets.[hash key (_buckets.Length - 1)] - 1
        value <- Unchecked.defaultof<'TValue>
        while uint32 i < uint32 entries.Length && not result do
            if eq key entries.[i].key then
                value <- entries.[i].value
                result <- true
            i <- entries.[i].next
        result
    member c.Contains(key) =
        let entries = _entries
        let mutable result = false
        let mutable i = _buckets.[hash key (_buckets.Length - 1)] - 1
        while uint32 i < uint32 entries.Length && not result do
            if eq key entries.[i].key then
                result <- true
            i <- entries.[i].next
        result
    member c.Remove(key : 'TKey) =
        let entries = _entries
        let bucketIndex = hash key (_buckets.Length - 1)
        let mutable entryIndex = _buckets.[bucketIndex] - 1
        let mutable lastIndex = -1
        let mutable result = false
        while entryIndex <> -1 && not result do
            let candidate = entries.[entryIndex]
            if eq candidate.key key then
                if lastIndex <> -1 then
                    // Fixup preceding element in chain to point to next (if any)
                    entries.[lastIndex].next <- candidate.next
                else
                    // Fixup bucket to new head (if any)
                    _buckets.[bucketIndex] <- candidate.next + 1
                entries.[entryIndex] <- Unchecked.defaultof<_>
                entries.[entryIndex].next <- -3 - _freeList // New head of free list
                _freeList <- entryIndex
                _count <- _count - 1
                result <- true
            lastIndex <- entryIndex
            entryIndex <- candidate.next
        result
    member c.GetOrAddValueRef(key : inref<'TKey>) : byref<'TValue> =
        let entries = _entries
        let mutable bucketIndex = hash key (_buckets.Length - 1)
        let mutable i = _buckets.[bucketIndex] - 1
        let mutable resultIndex = -1
        while uint32 i < uint32 entries.Length && resultIndex < 0 do
            if eq key entries.[i].key then
                resultIndex <- i                
            i <- entries.[i].next
        if resultIndex >= 0 then &entries.[resultIndex].value
        else 
            // AddKey()
            let mutable entries = _entries
            let entryIndex =
                if _freeList <> -1 then
                    let entryIndex = _freeList
                    _freeList <- -3 - entries.[_freeList].next
                    entryIndex
                else
                    if _count = entries.Length || entries.Length = 1 then
                        entries <- c.Resize()
                        bucketIndex <- hash key (_buckets.Length - 1)
                        // entry indexes were not changed by Resize
                    _count
            entries.[entryIndex].key <- key
            entries.[entryIndex].next <- _buckets.[bucketIndex] - 1
            _buckets.[bucketIndex] <- entryIndex + 1
            _count <- _count + 1
            &entries.[entryIndex].value
    member private c.Resize() =
        let mutable count = _count
        let newSize = _entries.Length * 2
        let entries = Array.zeroCreate<Entry<'TKey, 'TValue>> newSize
        Array.Copy(_entries, 0, entries, 0, count)
        let newBuckets = Array.zeroCreate entries.Length
        while count > 0 do
            count <- count - 1
            let bucketIndex = hash entries.[count].key (newBuckets.Length - 1)
            entries.[count].next <- newBuckets.[bucketIndex] - 1
            newBuckets.[bucketIndex] <- count + 1
        _buckets <- newBuckets
        _entries <- entries
        entries
    member c.GetEnumerator() = 
        new Enumerator<'TKey,'TValue>(c)
    interface IEnumerable<KeyValuePair<'TKey, 'TValue>> with
        member c.GetEnumerator() =
            new Enumerator<'TKey,'TValue>(c) :> IEnumerator<_>
    interface IEnumerable with
        member c.GetEnumerator() =
            new Enumerator<'TKey,'TValue>(c) :> IEnumerator

and Enumerator<'TKey, 'TValue
    when 'TKey :> IEquatable<'TKey>
    and 'TKey : equality> =
    val _dictionary : DictionarySlim<'TKey, 'TValue>
    val mutable _index : int
    val mutable _count : int
    val mutable _current : KeyValuePair<'TKey, 'TValue>
    new(dict) = {
        _dictionary = dict
        _index = 0
        _count = dict.Count
        _current = Unchecked.defaultof<_>
        }
    member c.MoveNext() =
        if c._count = 0 then
            c._current <- Unchecked.defaultof<_>
            false
        else
            c._count <- c._count - 1
            while c._dictionary.Entries.[c._index].next < -1 do
                c._index <- c._index + 1
            c._current <-
                new KeyValuePair<'TKey, 'TValue>(
                    c._dictionary.Entries.[c._index].key,
                    c._dictionary.Entries.[c._index].value)
            c._index <- c._index + 1
            true
    member c.Current = c._current
    member c.Reset() =
        c._index <- 0
        c._count <- c._dictionary.Count
        c._current <- Unchecked.defaultof<_>
    interface IEnumerator with
        member c.Current = c._current :> obj
        member c.MoveNext() = c.MoveNext()
        member c.Reset() = c.Reset()
    interface IEnumerator<KeyValuePair<'TKey, 'TValue>> with
        member c.Current = c._current
        member c.Dispose() = ()

/// Provides lookup by both key and index and allows adding
/// items while iterating.
type IndexedLookup<'k, 'v when 'k : equality>() =
    let items = List<'v>()
    let idToIndex = Dictionary<'k, int>()
    member c.Entries = idToIndex
    member c.Items = items
    member c.Count = items.Count
    member inline c.Item 
        with get i = items.[i]
        and set i x = items.[i] <- x
    member inline c.TryGetIndex(id, [<Out>] i : byref<_>) = 
        idToIndex.TryGetValue(id, &i)
    member c.TryGet(id, [<Out>] x : byref<_>) = 
        let mutable index = 0
        let result = c.TryGetIndex(id, &index)
        if result then x <- items.[index]
        result
    /// Returns index of new item
    member c.Add(id, x) =
        let index = items.Count
        items.Add(x)
        idToIndex.Add(id, index)
        index
    member c.Clear() =
        items.Clear()
        idToIndex.Clear()
