﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using FASTER.core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.PerfTest
{
    partial class PerfTest
    {
        static internal bool prompt = false;
        static readonly TestResult defaultTestResult = new TestResult();
        static TestParameters testParams;
        static string testFilename;
        static string resultsFilename;
        static string compareFirstFilename, compareSecondFilename;
        static ResultComparisonMode comparisonMode = ResultComparisonMode.None;
        static readonly List<string> mergeResultsFilespecs = new List<string>();
        static bool intersectResults;

        static void Main(string[] argv)
        {
            if (!ParseArgs(argv))
                return;

            if (comparisonMode != ResultComparisonMode.None)
            {
                TestResultComparisons.Compare(compareFirstFilename, compareSecondFilename, comparisonMode, resultsFilename);
                return;
            }
            if (mergeResultsFilespecs.Count > 0)
            {
                TestResults.Merge(mergeResultsFilespecs.ToArray(), intersectResults, resultsFilename);
                return;
            }

            ThreadPool.SetMinThreads(2 * Environment.ProcessorCount, 2 * Environment.ProcessorCount);
            TaskScheduler.UnobservedTaskException += (object sender, UnobservedTaskExceptionEventArgs e) =>
            {
                Console.WriteLine($"Unobserved task exception: {e.Exception}");
                e.SetObserved();
            };

            ExecuteTestRuns();
        }

        static void ExecuteTestRuns()
        {
            var results = new TestResults();
            if (!(testParams is null))
                testParams.Override(parseResult);
            var testRuns = (testParams is null ? new[] { new TestRun(parseResult) } : testParams.GetParamSweeps().Select(sweep => new TestRun(sweep))).ToArray();

            // This overall time includes overhead for allocating and distributing the keys, 
            // which has to be done per-test-run.
            var sw = new Stopwatch();
            sw.Start();

            int testNum = 0;
            foreach (var testRun in testRuns)
            {
                Console.WriteLine($"Test {++testNum} of {testRuns.Length}");

                // If running from a testfile, print command line for investigating testfile failures
                if (!(testParams is null))
                    Console.WriteLine(testRun.TestResult.Inputs);

                ExecuteTestRun(testRun);

                testRun.Finish();
                results.Add(testRun.TestResult);
            }

            sw.Stop();

            if (results.Results.Length == 0)
            {
                Console.WriteLine("No tests were run");
                return;
            }

            Console.WriteLine($"Completed {results.Results.Length} test run(s) in {TimeSpan.FromMilliseconds(sw.ElapsedMilliseconds)}");
            if (!string.IsNullOrEmpty(resultsFilename))
            {
                results.Write(resultsFilename);
               Console.WriteLine($"Results written to {resultsFilename}");
            }

            if (prompt)
            {
                Console.WriteLine("Press <ENTER> to end");
                Console.ReadLine();
            }
        }

        private static bool ExecuteTestRun(TestRun testRun)
        {
            Globals.KeySize = testRun.TestResult.Inputs.KeySize;
            Globals.ValueSize = testRun.TestResult.Inputs.ValueSize;

            if (testRun.TestResult.Inputs.UseVarLenKey)
            {
                var testInstance = new TestInstance<VarLenType>(testRun, new VarLenKeyManager(), new VarLenType.EqualityComparer());
                if (testRun.TestResult.Inputs.UseVarLenValue)
                {
                    return testInstance.Run<VarLenType, VarLenOutput, VarLenFunctions<VarLenType>>(null,
                                            new VariableLengthStructSettings<VarLenType, VarLenType>
                                            {
                                                keyLength = new VarLenTypeLength(),
                                                valueLength = new VarLenTypeLength()
                                            },
                                            new VarLenThreadValueRef(testRun.TestResult.Inputs.ThreadCount));
                }
                if (testRun.TestResult.Inputs.UseObjectValue)
                {
                    return testInstance.Run<ObjectType, ObjectTypeOutput, ObjectTypeFunctions<VarLenType>>(new SerializerSettings<VarLenType, ObjectType>
                                            {
                                                valueSerializer = () => new ObjectTypeSerializer(isKey: false)
                                            },
                                            new VariableLengthStructSettings<VarLenType, ObjectType>
                                            {
                                                keyLength = new VarLenTypeLength()
                                            },
                                            new ObjectThreadValueRef(testRun.TestResult.Inputs.ThreadCount));
                }

                // Value is Blittable
                bool run_VarLen_Key_BV_Value<TBV>() where TBV : IBlittableType, new()
                    => testInstance.Run<TBV, BlittableOutput<TBV>, BlittableFunctions<VarLenType, TBV>>
                        (null, new VariableLengthStructSettings<VarLenType, TBV> { keyLength = new VarLenTypeLength() },
                        new BlittableThreadValueRef<TBV>(testRun.TestResult.Inputs.ThreadCount));

                return Globals.ValueSize switch
                {
                    8 => run_VarLen_Key_BV_Value<BlittableType8>(),
                    16 => run_VarLen_Key_BV_Value<BlittableType16>(),
                    32 => run_VarLen_Key_BV_Value<BlittableType32>(),
                    64 => run_VarLen_Key_BV_Value<BlittableType64>(),
                    128 => run_VarLen_Key_BV_Value<BlittableType128>(),
                    256 => run_VarLen_Key_BV_Value<BlittableType256>(),
                    _ => throw new InvalidOperationException($"Unexpected Blittable data size: {Globals.ValueSize}")
                };
            }
            
            if (testRun.TestResult.Inputs.UseObjectKey)
            {
                var testInstance = new TestInstance<ObjectType>(testRun, new ObjectKeyManager(), new ObjectType.EqualityComparer());
                if (testRun.TestResult.Inputs.UseVarLenValue)
                {
                    return testInstance.Run<VarLenType, VarLenOutput, VarLenFunctions<ObjectType>>(new SerializerSettings<ObjectType, VarLenType>
                                            {
                                                keySerializer = () => new ObjectTypeSerializer(isKey: true)
                                            },
                                            new VariableLengthStructSettings<ObjectType, VarLenType>
                                            {
                                                valueLength = new VarLenTypeLength()
                                            },
                                            new VarLenThreadValueRef(testRun.TestResult.Inputs.ThreadCount));
                }
                if (testRun.TestResult.Inputs.UseObjectValue)
                {
                    return testInstance.Run<ObjectType, ObjectTypeOutput, ObjectTypeFunctions<ObjectType>>(new SerializerSettings<ObjectType, ObjectType>
                                            {
                                                keySerializer = () => new ObjectTypeSerializer(isKey: true),
                                                valueSerializer = () => new ObjectTypeSerializer(isKey: false)
                                            },
                                            null,
                                            new ObjectThreadValueRef(testRun.TestResult.Inputs.ThreadCount));
                }

                // Value is Blittable
                bool run_Object_Key_BV_Value<TBV>() where TBV : IBlittableType, new()
                    => testInstance.Run<TBV, BlittableOutput<TBV>, BlittableFunctions<ObjectType, TBV>>(
                        new SerializerSettings<ObjectType, TBV> { keySerializer = () => new ObjectTypeSerializer(isKey: true) }, null,
                        new BlittableThreadValueRef<TBV>(testRun.TestResult.Inputs.ThreadCount));

                return Globals.ValueSize switch
                {
                    8 => run_Object_Key_BV_Value<BlittableType8>(),
                    16 => run_Object_Key_BV_Value<BlittableType16>(),
                    32 => run_Object_Key_BV_Value<BlittableType32>(),
                    64 => run_Object_Key_BV_Value<BlittableType64>(),
                    128 => run_Object_Key_BV_Value<BlittableType128>(),
                    256 => run_Object_Key_BV_Value<BlittableType256>(),
                    _ => throw new InvalidOperationException($"Unexpected Blittable data size: {Globals.ValueSize}")
                };
            }

            // Key is Blittable

            if (testRun.TestResult.Inputs.UseVarLenValue)
            {
                bool run_BV_Key_VarLen_Value<TBV>() where TBV : struct, IBlittableType 
                    => new TestInstance<TBV>(testRun, new BlittableKeyManager<TBV>(), new BlittableEqualityComparer<TBV>())
                            .Run<VarLenType, VarLenOutput, VarLenFunctions<TBV>>(
                                null, new VariableLengthStructSettings<TBV, VarLenType> { valueLength = new VarLenTypeLength() },
                                new VarLenThreadValueRef(testRun.TestResult.Inputs.ThreadCount));

                return Globals.KeySize switch
                {
                    8 => run_BV_Key_VarLen_Value<BlittableType8>(),
                    16 => run_BV_Key_VarLen_Value<BlittableType16>(),
                    32 => run_BV_Key_VarLen_Value<BlittableType32>(),
                    64 => run_BV_Key_VarLen_Value<BlittableType64>(),
                    128 => run_BV_Key_VarLen_Value<BlittableType128>(),
                    256 => run_BV_Key_VarLen_Value<BlittableType256>(),
                    _ => throw new InvalidOperationException($"Unexpected Blittable data size: {Globals.KeySize}")
                };
            }

            if (testRun.TestResult.Inputs.UseObjectValue)
            {
                bool run_BV_Key_Object_Value<TBV>() where TBV : struct, IBlittableType
                    => new TestInstance<TBV>(testRun, new BlittableKeyManager<TBV>(), new BlittableEqualityComparer<TBV>())
                            .Run<VarLenType, VarLenOutput, VarLenFunctions<TBV>>(
                                null, new VariableLengthStructSettings<TBV, VarLenType> { valueLength = new VarLenTypeLength() },
                                new VarLenThreadValueRef(testRun.TestResult.Inputs.ThreadCount));

                return Globals.KeySize switch
                {
                    8 => run_BV_Key_Object_Value<BlittableType8>(),
                    16 => run_BV_Key_Object_Value<BlittableType16>(),
                    32 => run_BV_Key_Object_Value<BlittableType32>(),
                    64 => run_BV_Key_Object_Value<BlittableType64>(),
                    128 => run_BV_Key_Object_Value<BlittableType128>(),
                    256 => run_BV_Key_Object_Value<BlittableType256>(),
                    _ => throw new InvalidOperationException($"Unexpected Blittable data size: {Globals.KeySize}")
                };
            }

            // Key and value are Blittable

            bool run_BV_Key_BV_Value<TBVKey, TBVValue>() where TBVKey : struct, IBlittableType where TBVValue : IBlittableType, new()
                => new TestInstance<TBVKey>(testRun, new BlittableKeyManager<TBVKey>(), new BlittableEqualityComparer<TBVKey>())
                            .Run<TBVValue, BlittableOutput<TBVValue>, BlittableFunctions<TBVKey, TBVValue>>(
                                null, null, new BlittableThreadValueRef<TBVValue>(testRun.TestResult.Inputs.ThreadCount));

            return Globals.KeySize switch
            {
                8 => Globals.ValueSize switch
                    { 
                        8 => run_BV_Key_BV_Value<BlittableType8, BlittableType8>(),
                        16 => run_BV_Key_BV_Value<BlittableType8, BlittableType16>(),
                        32 => run_BV_Key_BV_Value<BlittableType8, BlittableType32>(),
                        64 => run_BV_Key_BV_Value<BlittableType8, BlittableType64>(),
                        128 => run_BV_Key_BV_Value<BlittableType8, BlittableType128>(),
                        256 => run_BV_Key_BV_Value<BlittableType8, BlittableType256>(),
                        _ => throw new InvalidOperationException($"Unexpected Blittable data size: {Globals.ValueSize}")
                    },
                16 => Globals.ValueSize switch
                    {
                        8 => run_BV_Key_BV_Value<BlittableType16, BlittableType8>(),
                        16 => run_BV_Key_BV_Value<BlittableType16, BlittableType16>(),
                        32 => run_BV_Key_BV_Value<BlittableType16, BlittableType32>(),
                        64 => run_BV_Key_BV_Value<BlittableType16, BlittableType64>(),
                        128 => run_BV_Key_BV_Value<BlittableType16, BlittableType128>(),
                        256 => run_BV_Key_BV_Value<BlittableType16, BlittableType256>(),
                        _ => throw new InvalidOperationException($"Unexpected Blittable data size: {Globals.ValueSize}")
                    },
                32 => Globals.ValueSize switch
                    {
                        8 => run_BV_Key_BV_Value<BlittableType32, BlittableType8>(),
                        16 => run_BV_Key_BV_Value<BlittableType32, BlittableType16>(),
                        32 => run_BV_Key_BV_Value<BlittableType32, BlittableType32>(),
                        64 => run_BV_Key_BV_Value<BlittableType32, BlittableType64>(),
                        128 => run_BV_Key_BV_Value<BlittableType32, BlittableType128>(),
                        256 => run_BV_Key_BV_Value<BlittableType32, BlittableType256>(),
                        _ => throw new InvalidOperationException($"Unexpected Blittable data size: {Globals.ValueSize}")
                    },
                64 => Globals.ValueSize switch
                    {
                        8 => run_BV_Key_BV_Value<BlittableType64, BlittableType8>(),
                        16 => run_BV_Key_BV_Value<BlittableType64, BlittableType16>(),
                        32 => run_BV_Key_BV_Value<BlittableType64, BlittableType32>(),
                        64 => run_BV_Key_BV_Value<BlittableType64, BlittableType64>(),
                        128 => run_BV_Key_BV_Value<BlittableType64, BlittableType128>(),
                        256 => run_BV_Key_BV_Value<BlittableType64, BlittableType256>(),
                        _ => throw new InvalidOperationException($"Unexpected Blittable data size: {Globals.ValueSize}")
                    },
                128 => Globals.ValueSize switch
                    {
                        8 => run_BV_Key_BV_Value<BlittableType128, BlittableType8>(),
                        16 => run_BV_Key_BV_Value<BlittableType128, BlittableType16>(),
                        32 => run_BV_Key_BV_Value<BlittableType128, BlittableType32>(),
                        64 => run_BV_Key_BV_Value<BlittableType128, BlittableType64>(),
                        128 => run_BV_Key_BV_Value<BlittableType128, BlittableType128>(),
                        256 => run_BV_Key_BV_Value<BlittableType128, BlittableType256>(),
                        _ => throw new InvalidOperationException($"Unexpected Blittable data size: {Globals.ValueSize}")
                    },
                256 => Globals.ValueSize switch
                    {
                        8 => run_BV_Key_BV_Value<BlittableType256, BlittableType8>(),
                        16 => run_BV_Key_BV_Value<BlittableType256, BlittableType16>(),
                        32 => run_BV_Key_BV_Value<BlittableType256, BlittableType32>(),
                        64 => run_BV_Key_BV_Value<BlittableType256, BlittableType64>(),
                        128 => run_BV_Key_BV_Value<BlittableType256, BlittableType128>(),
                        256 => run_BV_Key_BV_Value<BlittableType256, BlittableType256>(),
                        _ => throw new InvalidOperationException($"Unexpected Blittable data size: {Globals.ValueSize}")
                    },
                _ => throw new InvalidOperationException($"Unexpected Blittable data size: {Globals.KeySize}")
            };
        }
    }
}