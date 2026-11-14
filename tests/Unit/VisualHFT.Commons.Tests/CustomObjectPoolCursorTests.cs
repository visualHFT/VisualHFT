using System;
using System.Collections.Generic;
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
    }
}
