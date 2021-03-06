#region COPYRIGHT

/*
 *     Copyright 2009-2012 Yuri Astrakhan  (<Firstname><Lastname>@gmail.com)
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 *
 */

#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Do not disable these Resharper checks in your code. Demo purposes only.
// ReSharper disable InconsistentNaming

namespace NYurik.TimeSeriesDb.Samples
{
    internal class Demo_01_Simple : ISample
    {
        #region ISample Members

        public void Run()
        {
            string filename = GetType().Name + ".bts";
            if (File.Exists(filename)) File.Delete(filename);

            // Create new BinSeriesFile file that stores a sequence of ItemLngDbl structs
            // The file is indexed by a long value inside ItemLngDbl marked with the [Index] attribute.
            using (var bf = new BinSeriesFile<long, ItemLngDbl>(filename))
            {
                //
                // Initialize new file parameters and create it
                //
                bf.UniqueIndexes = true; // enforce index uniqueness
                bf.Tag = "Sample Data"; // optionally provide a tag to store in the file header
                bf.InitializeNewFile(); // Finish new file initialization and create an empty file


                // 
                // Set up data generator to generate 10 items starting with index 3
                //
                IEnumerable<ArraySegment<ItemLngDbl>> data = Utils.GenerateData(3, 10, i => new ItemLngDbl(i, i/100.0));


                //
                // Append data to the file
                //
                bf.AppendData(data);


                //
                // Read all data and print it using Stream() - one value at a time
                // This method is slower than StreamSegments(), but easier to use for simple one-value iteration
                //
                Console.WriteLine(" ** Content of file {0} after the first append", filename);
                Console.WriteLine("FirstIndex = {0}, LastIndex = {1}", bf.FirstIndex, bf.LastIndex);
                foreach (ItemLngDbl val in bf.Stream())
                    Console.WriteLine(val);
            }

            // Re-open the file, allowing data modifications
            // IWritableFeed<,> interface is better as it will work with compressed files as well
            using (var bf = (IWritableFeed<long, ItemLngDbl>) BinaryFile.Open(filename, true))
            {
                // Append a few more items with different ItemLngDbl.Value to tell them appart
                IEnumerable<ArraySegment<ItemLngDbl>> data = Utils.GenerateData(10, 10, i => new ItemLngDbl(i, i/25.0));

                // New data indexes will overlap with existing, so allow truncating old data
                bf.AppendData(data, true);

                // Print values
                Console.WriteLine("\n ** Content of file {0} after the second append", filename);
                Console.WriteLine("FirstIndex = {0}, LastIndex = {1}", bf.FirstIndex, bf.LastIndex);
                foreach (ItemLngDbl val in bf.Stream())
                    Console.WriteLine(val);
            }

            // Re-open the file for reading only (file can be opened for reading in parallel, but only one write)
            // IEnumerableFeed<,> interface is better as it will work with compressed files as well
            using (var bf = (IWritableFeed<long, ItemLngDbl>)BinaryFile.Open(filename, true))
            {
                // Show first item with index >= 5
                Console.WriteLine(
                    "\nFirst item on or after index 5 is {0}\n",
                    bf.Stream(5, maxItemCount: 1).First());

                // Show last item with index < 7 (iterate backwards)
                Console.WriteLine(
                    "Last item before index 7 is {0}\n",
                    bf.Stream(7, inReverse: true, maxItemCount: 1).First());

                // Average of values for indexes >= 4 and < 8
                Console.WriteLine(
                    "Average of values for indexes >= 4 and < 8 is {0}\n",
                    bf.Stream(4, 8).Average(i => i.Value));

                // Sum of the first 3 values with index less than 18 and going backwards
                Console.WriteLine(
                    "Sum of the first 3 values with index less than 18 and going backwards is {0}\n",
                    bf.Stream(18, maxItemCount: 3, inReverse: true).Sum(i => i.Value));
            }

            // cleanup
            File.Delete(filename);
        }

        #endregion
    }
}