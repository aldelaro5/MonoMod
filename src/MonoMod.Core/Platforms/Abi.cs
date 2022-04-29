﻿using MonoMod.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MonoMod.Core.Platforms {
    public enum TypeClassification {
        ByVal,
        ByRef,
    }

    public delegate TypeClassification Classifier(Type type, bool isReturn);

    public enum SpecialArgumentKind {
        ThisPointer,
        ReturnBuffer,
        GenericContext,
        UserArguments, // yes, this is needed. On x86, the generic context goes AFTER the user arguments.
    }

    // TODO: include information about how many registers are used for parameter passing
    public readonly record struct Abi(
        ReadOnlyMemory<SpecialArgumentKind> ArgumentOrder,
        Classifier Classifier,
        bool ReturnsReturnBuffer
        ) {

        public TypeClassification Classify(Type type, bool isReturn) {
            Helpers.ThrowIfNull(type);

            if (type == typeof(void))
                return TypeClassification.ByVal;
            if (!type.IsValueType)
                return TypeClassification.ByVal;
            if (type.IsPointer)
                return TypeClassification.ByVal;
            if (type.IsByRef)
                return TypeClassification.ByVal;

            return Classifier(type, isReturn);
        }
    }
}
