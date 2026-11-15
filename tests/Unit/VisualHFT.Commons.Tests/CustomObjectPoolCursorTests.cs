using System;
using System.Collections.Generic;
using System.Reflection;
using VisualHFT.Commons.Pools;
using Xunit;

namespace VisualHFT.Commons.Tests
{
    /// <summary>
    /// A get takes from the slots in front of a segment's get cursor and a return writes at its return cursor.
    /// A segment's ready objects fill its first slots, so the return cursor has to start after them: returns
    /// that land behind the get cursor are never found, and the pool then creates a new object on almost every
    /// request although its available count says objects exist. Each test uses its own pool.
    /// </summary>
    public class CustomObjectPoolCursorTests
    {
        public sealed class Tracked
        {
            public int Value;
        }

        [Fact]
        public void FreshPool_OneInOneOut_CreatesNothingAfterTheFirstObject()
        {
            using var pool = new CustomObjectPool<Tracked>(65_536);
            var first = pool.Get();
            pool.Return(first);
            long created0 = pool.TotalCreated;

            for (int k = 0; k < 100_000; k++)
            {
                pool.Return(pool.Get());
            }

            Assert.Equal(0, pool.TotalCreated - created0);
        }

        [Theory]
        [InlineData(100)]
        [InlineData(6_400)]
        public void DrainThenReturnAll_LaterRequestsReuseTheReturnedObjects(int burst)
        {
            using var pool = new CustomObjectPool<Tracked>(65_536);
            var held = new List<Tracked>(burst);
            for (int i = 0; i < burst; i++)
            {
                held.Add(pool.Get());
            }

            var returned = new HashSet<Tracked>(held, ReferenceEqualityComparer.Instance);
            foreach (var item in held)
            {
                pool.Return(item);
            }

            long created0 = pool.TotalCreated;
            for (int k = 0; k < 50_000; k++)
            {
                var item = pool.Get();
                Assert.True(returned.Contains(item),
                    $"Request {k:N0} after a burst of {burst:N0} came back got an object the pool had not been given back.");
                pool.Return(item);
            }

            Assert.Equal(0, pool.TotalCreated - created0);
        }

        // A grown segment also starts with ready objects in its first slots; returns that overflow the first
        // segment go there and must land where its get cursor will look.
        [Fact]
        public void GrownSegment_ReturnsOverflowingTheFirstSegment_AreReused()
        {
            using var pool = new CustomObjectPool<Tracked>(1_024);
            int count = pool.SegmentSize + 50;
            var held = new List<Tracked>(count);
            for (int i = 0; i < count; i++)
            {
                held.Add(pool.Get());
            }

            Assert.True(pool.SegmentCount >= 2, "The first request past the ready objects should have grown the pool.");
            foreach (var item in held)
            {
                pool.Return(item);
            }

            var returned = new HashSet<Tracked>(held, ReferenceEqualityComparer.Instance);
            long created0 = pool.TotalCreated;
            for (int i = 0; i < count; i++)
            {
                var item = pool.Get();
                Assert.True(returned.Contains(item), $"Request {i:N0} got an object that was never returned.");
            }

            Assert.Equal(0, pool.TotalCreated - created0);
        }

        // Concurrent gets and returns can leave a returned object behind both places a get looks: in front of the
        // get cursor and just behind the return cursor. The available count still includes it, so every later get
        // scans that segment, misses, and creates a new object instead. Every counted object must be found again
        // within a bounded number of requests, not only when the get cursor next goes round the whole segment.
        [Theory]
        [InlineData(4, 55)]
        [InlineData(4, 85)]
        [InlineData(8, 85)]
        public void ConcurrentGetsAndReturns_EveryCountedObjectIsFoundAgainWithinBoundedRequests(int threads, int getPercent)
        {
            using var pool = new CustomObjectPool<Tracked>(65_536);
            const int opsPerThread = 200_000;
            var held = new List<Tracked>[threads];
            var workers = new System.Threading.Tasks.Task[threads];
            using var start = new System.Threading.ManualResetEventSlim(false);
            for (int t = 0; t < threads; t++)
            {
                int id = t;
                held[id] = new List<Tracked>(4_096);
                workers[id] = System.Threading.Tasks.Task.Run(() =>
                {
                    var mine = held[id];
                    var rnd = new Random(17 + id);
                    start.Wait();
                    for (int k = 0; k < opsPerThread; k++)
                    {
                        if (mine.Count == 0 || rnd.Next(100) < getPercent)
                        {
                            mine.Add(pool.Get());
                        }
                        else
                        {
                            int i = rnd.Next(mine.Count);
                            var item = mine[i];
                            mine[i] = mine[mine.Count - 1];
                            mine.RemoveAt(mine.Count - 1);
                            pool.Return(item);
                        }
                    }
                });
            }
            start.Set();
            System.Threading.Tasks.Task.WaitAll(workers);

            // Quiet from here on: one thread. The objects still held stay out, as in a book that keeps its orders.
            // The pool still counts the rest. Without the sweep window in the get path, 7 to 63 of them stayed counted
            // in every run, most out of reach; with it the concurrent gets usually find them all themselves.
            int available = pool.AvailableObjects;

            // A request that misses one segment's counted objects may create an object; the bound allows one
            // window of 64 slots per miss to sweep each segment once.
            int bound = available + pool.SegmentCount * (pool.SegmentSize / 64);
            int found = 0;
            var taken = new List<Tracked>(bound);
            for (int k = 0; k < bound && found < available; k++)
            {
                long createdBefore = pool.TotalCreated;
                var item = pool.Get();
                taken.Add(item);
                if (pool.TotalCreated == createdBefore)
                {
                    found++;
                }
            }

            Assert.True(found == available,
                $"Only {found:N0} of the {available:N0} counted objects were found again in {bound:N0} requests.");
        }

        // A block of counted objects outside both windows (the state concurrent gets and returns can leave, built
        // directly here). One sweep of the segment must find all of it: a request that misses may create an object,
        // but no more of them than one 64-slot window per slot range of the segment.
        [Theory]
        [InlineData(200)]
        [InlineData(1_000)]
        [InlineData(5_000)]
        public void StrandedBlock_IsFoundWithinOneSweepOfTheSegment(int block)
        {
            using var pool = new CustomObjectPool<Tracked>(65_536);
            StrandBlock(pool, block, live: 100);
            int available = pool.AvailableObjects;
            long created0 = pool.TotalCreated;

            var taken = new List<Tracked>(available + pool.SegmentSize);
            for (int k = 0; k < 4 * pool.SegmentSize && pool.AvailableObjects > 0; k++)
            {
                taken.Add(pool.Get());
            }

            long created = pool.TotalCreated - created0;
            Assert.Equal(0, pool.AvailableObjects);
            Assert.True(created <= pool.SegmentSize / 64,
                $"Finding a stranded block of {block:N0} created {created:N0} objects; one sweep of the segment allows {pool.SegmentSize / 64:N0}.");
        }

        // After the pool has found a stranded block, a get must find its object at the get cursor again: not at the
        // end of the first window (a cursor left trailing its objects reads up to 64 slots on every get), and not
        // through the rescan or the sweep (up to 192). A return must likewise find a free slot at the return cursor,
        // not past a run of occupied slots it reads on every return.
        [Theory]
        [InlineData(200)]
        [InlineData(1_000)]
        [InlineData(5_000)]
        public void AfterAStrandedBlock_OneInOneOutGetsFindTheirObjectAtTheGetCursor(int block)
        {
            using var pool = new CustomObjectPool<Tracked>(65_536);
            StrandBlock(pool, block, live: 100);
            for (int k = 0; k < 200_000; k++)
            {
                pool.Return(pool.Get());
            }

            const BindingFlags Np = BindingFlags.NonPublic | BindingFlags.Instance;
            var type = pool.GetType();
            var segment = ((Tracked[][])type.GetField("_segments", Np)!.GetValue(pool)!)[0];
            var getPositions = (long[])type.GetField("_getPositions", Np)!.GetValue(pool)!;
            var returnPositions = (long[])type.GetField("_returnPositions", Np)!.GetValue(pool)!;
            long slow0 = pool.SlowGets;
            long created0 = pool.TotalCreated;
            int notAtCursor = 0;
            int notAtReturnCursor = 0;
            for (int k = 0; k < 100_000; k++)
            {
                if (segment[getPositions[0] & (segment.Length - 1)] == null)
                {
                    notAtCursor++;
                }
                var item = pool.Get();
                if (segment[returnPositions[0] & (segment.Length - 1)] != null)
                {
                    notAtReturnCursor++;
                }
                pool.Return(item);
            }

            Assert.Equal(0, pool.TotalCreated - created0);
            Assert.True(pool.SlowGets - slow0 == 0,
                $"{pool.SlowGets - slow0:N0} of 100,000 gets needed the rescan or the sweep after the pool had recovered.");
            Assert.True(notAtCursor == 0,
                $"For {notAtCursor:N0} of 100,000 gets the slot at the get cursor was empty, so the get read further slots.");
            Assert.True(notAtReturnCursor == 0,
                $"For {notAtReturnCursor:N0} of 100,000 returns the slot at the return cursor was taken, so the return read further slots.");
        }

        // A get cursor left 40 slots before its objects (built directly) finds each object at the 41st slot it reads.
        // The first get that does so must move the cursor to the object, or one-in-one-out keeps that gap forever.
        [Fact]
        public void GetCursorTrailingItsObjects_IsAtTheObjectsAfterOneGet()
        {
            using var pool = new CustomObjectPool<Tracked>(65_536);
            const BindingFlags Np = BindingFlags.NonPublic | BindingFlags.Instance;
            var type = pool.GetType();
            var segment = ((Tracked[][])type.GetField("_segments", Np)!.GetValue(pool)!)[0];
            var counts = (long[])type.GetField("_segmentAvailableCounts", Np)!.GetValue(pool)!;
            var getPositions = (long[])type.GetField("_getPositions", Np)!.GetValue(pool)!;
            var returnPositions = (long[])type.GetField("_returnPositions", Np)!.GetValue(pool)!;
            Array.Clear(segment);
            for (int i = 0; i < 100; i++)
            {
                segment[1_040 + i] = new Tracked();
            }
            counts[0] = 100;
            getPositions[0] = 1_000;
            returnPositions[0] = 1_140;

            pool.Return(pool.Get());
            int notAtCursor = 0;
            for (int k = 0; k < 10_000; k++)
            {
                if (segment[getPositions[0] & (segment.Length - 1)] == null)
                {
                    notAtCursor++;
                }
                pool.Return(pool.Get());
            }

            Assert.True(notAtCursor == 0,
                $"For {notAtCursor:N0} of 10,000 gets the slot at the get cursor was empty, so the get read further slots.");
        }

        [Fact]
        public void SlowGets_CountsAGetServedByTheSweep()
        {
            using var pool = new CustomObjectPool<Tracked>(65_536);
            StrandBlock(pool, block: 1, live: 0);
            long slow0 = pool.SlowGets;
            long created0 = pool.TotalCreated;

            do
            {
                pool.Get();
            }
            while (pool.TotalCreated - created0 < pool.SegmentSize && pool.AvailableObjects > 0);

            Assert.Equal(0, pool.AvailableObjects);
            Assert.Equal(1, pool.SlowGets - slow0);
        }

        // Reset() zeroes the statistics. It must leave each segment's objects between its get and return cursors,
        // with the count equal to the objects, so the gets after it take them in order and nothing is created.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Reset_LeavesTheObjectsBetweenTheCursors_AndKeepsEveryObject(int seed)
        {
            using var pool = new CustomObjectPool<Tracked>(65_536);
            var rnd = new Random(seed);
            int holdCap = 1_000 + rnd.Next(40_000);
            var held = new List<Tracked>();
            for (int k = 0; k < 300_000; k++)
            {
                if (held.Count == 0 || (held.Count < holdCap && rnd.Next(100) < 55))
                {
                    held.Add(pool.Get());
                }
                else
                {
                    int i = rnd.Next(held.Count);
                    var item = held[i];
                    held[i] = held[held.Count - 1];
                    held.RemoveAt(held.Count - 1);
                    pool.Return(item);
                }
            }
            foreach (var item in held)
            {
                pool.Return(item);
            }

            var before = PooledObjects(pool);
            pool.Reset();
            var after = PooledObjects(pool);
            Assert.True(before.SetEquals(after), $"Reset kept {after.Count:N0} of the {before.Count:N0} pooled objects.");

            const BindingFlags Np = BindingFlags.NonPublic | BindingFlags.Instance;
            var type = pool.GetType();
            var segments = (Tracked[][])type.GetField("_segments", Np)!.GetValue(pool)!;
            var counts = (long[])type.GetField("_segmentAvailableCounts", Np)!.GetValue(pool)!;
            var getPositions = (long[])type.GetField("_getPositions", Np)!.GetValue(pool)!;
            var returnPositions = (long[])type.GetField("_returnPositions", Np)!.GetValue(pool)!;
            for (int s = 0; s < pool.SegmentCount; s++)
            {
                var segment = segments[s];
                int objects = 0;
                foreach (var slot in segment)
                {
                    if (slot != null)
                    {
                        objects++;
                    }
                }
                Assert.Equal(objects, counts[s]);
                Assert.Equal(objects, returnPositions[s] - getPositions[s]);
                for (long p = getPositions[s]; p < returnPositions[s]; p++)
                {
                    Assert.True(segment[p & (segment.Length - 1)] != null, $"Segment {s}: slot {p} between the cursors is empty after Reset.");
                }
            }

            int available = pool.AvailableObjects;
            long created0 = pool.TotalCreated;
            long slow0 = pool.SlowGets;
            for (int k = 0; k < available; k++)
            {
                pool.Get();
            }
            Assert.Equal(0, pool.TotalCreated - created0);
            Assert.Equal(0, pool.SlowGets - slow0);
        }

        private static void StrandBlock(CustomObjectPool<Tracked> pool, int block, int live)
        {
            // Segment 0 holds a run of `live` objects just behind the return cursor, the get cursor 200 slots past
            // them, and a block of `block` counted objects in the middle of the segment, outside every window.
            const BindingFlags Np = BindingFlags.NonPublic | BindingFlags.Instance;
            var type = pool.GetType();
            var segment = ((Tracked[][])type.GetField("_segments", Np)!.GetValue(pool)!)[0];
            var counts = (long[])type.GetField("_segmentAvailableCounts", Np)!.GetValue(pool)!;
            var getPositions = (long[])type.GetField("_getPositions", Np)!.GetValue(pool)!;
            var returnPositions = (long[])type.GetField("_returnPositions", Np)!.GetValue(pool)!;
            Array.Clear(segment);
            int blockStart = segment.Length / 2;
            for (int i = 0; i < block; i++)
            {
                segment[blockStart + i] = new Tracked();
            }
            for (int i = 0; i < live; i++)
            {
                segment[1_000 + i] = new Tracked();
            }
            counts[0] = block + live;
            returnPositions[0] = 1_000 + live;
            getPositions[0] = 1_000 + live + 200;
        }

        private static HashSet<Tracked> PooledObjects(CustomObjectPool<Tracked> pool)
        {
            const BindingFlags Np = BindingFlags.NonPublic | BindingFlags.Instance;
            var segments = (Tracked[][])pool.GetType().GetField("_segments", Np)!.GetValue(pool)!;
            var set = new HashSet<Tracked>(ReferenceEqualityComparer.Instance);
            for (int s = 0; s < pool.SegmentCount; s++)
            {
                foreach (var slot in segments[s])
                {
                    if (slot != null)
                    {
                        set.Add(slot);
                    }
                }
            }
            return set;
        }
    }
}
