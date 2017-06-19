﻿using System;
using System.Collections.Generic;

namespace Tests.Diagnostics
{
    public class EmptyNullableValueAccess
    {
        private IEnumerable<TestClass> numbers = new[]
        {
            new TestClass { Number = 42 },
            new TestClass(),
            new TestClass { Number = 1 },
            new TestClass { Number = null }
        };

        private class TestClass
        {
            public int? Number { get; set; }
        }

        void Nameof(object o)
        {
            if (o == null)
            {
            }

            if (o == nameof(Object))
            {
                o.ToString(); // Compliant
            }
        }

        public void SetI0()
        {
            i0 = 42;
        }

        public void TestNull()
        {
            // todo VT: problem with Nullable <- NULL assignment.
            // the same would happen in case of a cast, implicit cast, 'as', or 
            // int? i = 5; (5 is a SV and not a NullableSV)
            // the SymbolicValue should be converted to NullableSymbolicValue
            int? i1 = null;
            if (i1.HasValue)
            {
                Console.WriteLine(i1.Value);
            }

            Console.WriteLine(i1.Value); // Noncompliant {{'i1' is null on at least one execution path.}}
//                            ^^^^^^^^
        }

        public IEnumerable<TestClass> TestEnumerableExpressionWithCompilableCode() => numbers.OrderBy(i => i.Number.HasValue).ThenBy(i => i.Number);
        public IEnumerable<int> TestEnumerableExpressionWithNonCompilableCode() => numbers.OrderBy(i => i.Number.HasValue).ThenBy(i => i.Number);

        public void TestNonNull()
        {
            int? i1 = 42;
            if (i1.HasValue)
            {
                Console.WriteLine(i1.Value);
            }

            Console.WriteLine(i1.Value);
        }

        public void TestNullConstructor()
        {
            int? i2 = new Nullable<int>();
            if (i2.HasValue)
            {
                Console.WriteLine(i2.Value);
            }

            Console.WriteLine(i2.Value); // Noncompliant
        }

        public void TestNonNullConstructor()
        {
            int? i1 = new Nullable<int>(42);
            if (i1.HasValue)
            {
                Console.WriteLine(i1.Value);
            }

            Console.WriteLine(i1.Value);
        }

        public void TestComplexCondition(int? i3, double? d3, float? f3)
        {
            if (i3.HasValue && i3.Value == 42)
            {
                Console.WriteLine();
            }

            if (!i3.HasValue && i3.Value == 42) // Noncompliant
//                              ^^^^^^^^
            {
                Console.WriteLine();
            }

            if (!d3.HasValue)
            {
                Console.WriteLine(d3.Value); // Noncompliant
            }

            if (f3 == null)
            {
                Console.WriteLine(f3.Value); // Noncompliant
            }
        }
    }
}
