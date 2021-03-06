﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tests.TestCases
{
    public partial class StaticFieldInitializerOrder
    {
        public static Action<int> A = (i) => { var x = i + StaticFieldInitializerOrder.Y; }; // Okay??? Might or might not. For no we don't report on it
        public static int X = Y; // Noncompliant; Y at this time is still assigned default(int), i.e. 0
//                          ^^^
        public static int X2 = M(StaticFieldInitializerOrder.Y); // Noncompliant; Y at this time is still assigned default(int), i.e. 0
        public static int Y = 42;
        public static int Z = Y; // Okay
        public static int V = W; // Noncompliant {{Move this field's initializer into a static constructor.}}
        public static int U = Const; // Compliant
        public const int Const = 5;

        public int nonStat = W;

        public static int M(int i) { return i; }
    }

    public partial class StaticFieldInitializerOrder
    {
        public static int W = 2;
    }
}
