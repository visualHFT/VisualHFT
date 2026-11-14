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
    }
}
