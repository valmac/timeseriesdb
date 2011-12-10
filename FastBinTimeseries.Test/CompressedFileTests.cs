using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NYurik.FastBinTimeseries.CommonCode;
using NYurik.FastBinTimeseries.Serializers.BlockSerializer;

namespace NYurik.FastBinTimeseries.Test
{
    using BinCompressedFile = BinCompressedSeriesFile<UtcDateTime, _DatetimeByte_SeqPk1>;
    using BinUncompressedFile = BinTimeseriesFile<_DatetimeByte_SeqPk1>;

    // ReSharper disable PossibleMultipleEnumeration

    [TestFixture]
    public class CompressedFileTests : TestsBase
    {
        private void Run(string name, int itemCount,
                         Func<string, IEnumerableFeed<UtcDateTime, _DatetimeByte_SeqPk1>> newFile,
                         Action<IEnumerableFeed<UtcDateTime, _DatetimeByte_SeqPk1>> update,
                         Action<IEnumerableFeed<UtcDateTime, _DatetimeByte_SeqPk1>> init)
        {
            string fileName = GetBinFileName();
            using (
                IEnumerableFeed<UtcDateTime, _DatetimeByte_SeqPk1> f
                    = !AllowCreate
                          ? (IEnumerableFeed<UtcDateTime, _DatetimeByte_SeqPk1>) BinaryFile.Open(fileName, false)
                          : newFile(fileName))
            {
                if (update != null)
                    update(f);

                IEnumerable<ArraySegment<_DatetimeByte_SeqPk1>> newData =
                    TestUtils.GenerateDataStream(_DatetimeByte_SeqPk1.New, itemCount, 0, itemCount);

                List<_DatetimeByte_SeqPk1> expected = newData.StreamSegmentValues().ToList();
                Assert.AreEqual(itemCount, expected.Count);
                _DatetimeByte_SeqPk1[] expectedRev = expected.ToArray();
                Array.Reverse(expectedRev);

                if (AllowCreate)
                {
                    init(f);
                    f.AppendData(newData.Select(i => new ArraySegment<_DatetimeByte_SeqPk1>(i.Array, 0, i.Count)));
                }

                _DatetimeByte_SeqPk1[] empty = _DatetimeByte_SeqPk1.Empty;

                TestUtils.CollectionAssertEqual(
                    empty, f.Stream(UtcDateTime.MinValue, inReverse: true),
                    "nothing before 0 {0}", name);

                TestUtils.CollectionAssertEqual(
                    empty, f.Stream(UtcDateTime.MaxValue), "nothing after max {0}", name);

                if (itemCount <= 0)
                {
                    Assert.IsNull(f.FirstFileIndex, "null FirstInd {0}", name);
                    Assert.IsNull(f.LastFileIndex, "null LastInd {0}", name);
                    TestUtils.CollectionAssertEqual(empty, f.Stream(UtcDateTime.MinValue), "empty forward {0}", name);
                    TestUtils.CollectionAssertEqual(
                        empty, f.Stream(UtcDateTime.MinValue, inReverse: true), "empty backward {0}", name);
                    return;
                }

                Assert.AreEqual(expected[0].a, f.FirstFileIndex, name + " first");
                Assert.AreEqual(expected[itemCount - 1].a, f.LastFileIndex, "last {0}", name);

                TestUtils.CollectionAssertEqual(expected, f.Stream(UtcDateTime.MinValue), "full forward {0}", name);
                TestUtils.CollectionAssertEqual(
                    expectedRev, f.Stream(UtcDateTime.MaxValue, inReverse: true), "full backward {0}", name);

                const int skipStart = 0;
                const int takeStart = 0;

                const int maxSkipCount = 50;

                int maxSkip = Math.Min(maxSkipCount, itemCount);
                for (int skip = skipStart; skip < maxSkip; skip++)
                {
                    int maxTake = Math.Min(maxSkipCount, itemCount - maxSkip + 1);
                    for (int take = takeStart; take < maxTake; take++)
                    {
                        TestUtils.CollectionAssertEqual(
                            expected.Skip(skip).Take(take), f.Stream(expected[skip].a, maxItemCount: take),
                            "skip {1} take {2} {0}", name, skip, take);

                        TestUtils.CollectionAssertEqual(
                            expectedRev.Skip(skip).Take(take),
                            f.Stream(expectedRev[skip].a, maxItemCount: take, inReverse: true),
                            "backward skip {1} take {2} {0}", name, skip, take);

                        if (itemCount < take)
                            TestUtils.CollectionAssertEqual(
                                expected.Skip(skip).Take(take - 1),
                                f.Stream(expected[skip].a.AddSeconds(1), maxItemCount: take - 1),
                                "next tick skip {1} take ({2}-1) {0}", name, skip, take);
                        if (itemCount < skip)
                            TestUtils.CollectionAssertEqual(
                                expectedRev.Skip(skip - 1).Take(take),
                                f.Stream(expectedRev[skip].a.AddSeconds(-1), maxItemCount: take, inReverse: true),
                                "next tick backward skip ({1}-1) take {2} {0}", name, skip, take);
                    }
                }
            }
        }

        [Test, Combinatorial]
        public void CompressedGeneralTest(
            [Values(0, 1, 2, 3, 4, 5, 10, 100, 10000)] int itemCount,
            [Values(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 20, 200, 10000)] int blockSizeExtra,
            [Values(true, false)] bool enableCache)
        {
            Run(
                string.Format(
                    "in itemCount={0}, blockSizeExtra={1}, cache={2}", itemCount, blockSizeExtra, enableCache),
                itemCount,
                fileName =>
                    {
                        var bf = new BinCompressedFile(fileName)
                                     {UniqueIndexes = true};

                        bf.BlockSize = bf.FieldSerializer.RootField.GetMaxByteSize() + CodecBase.ReservedSpace
                                       + blockSizeExtra;
                        return bf;
                    },
                f => ((BinCompressedFile) f).BinarySearchCacheSize = enableCache ? 0 : -1,
                f => ((BinCompressedFile) f).InitializeNewFile()
                );
        }

        [Test]
        public void AppendTest()
        {
            throw new NotImplementedException("Need to implement incremental file addition");
        }
        
        [Test, Combinatorial]
        public void UncompressedGeneralTimeseries(
            [Values(0, 1, 2, 3, 4, 5, 10, 100, 10000)] int itemCount,
            [Values(true, false)] bool enableCache)
        {
            Run(
                string.Format("in itemCount={0}, cache={1}", itemCount, enableCache),
                itemCount,
                fileName => new BinTimeseriesFile<_DatetimeByte_SeqPk1>(fileName) {UniqueIndexes = true},
                f => ((BinUncompressedFile) f).BinarySearchCacheSize = enableCache ? 0 : -1,
                f => ((BinUncompressedFile) f).InitializeNewFile()
                );
        }
    }
}