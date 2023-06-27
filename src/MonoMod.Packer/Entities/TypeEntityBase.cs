﻿using AsmResolver;
using System.Collections.Immutable;

namespace MonoMod.Packer.Entities {
    internal abstract class TypeEntityBase : EntityBase {
        protected TypeEntityBase(TypeEntityMap map) : base(map) {

        }

        public abstract Utf8String? Namespace { get; }


        private TypeMergeMode lazyTypeMergeMode;
        public TypeMergeMode TypeMergeMode {
            get {
                if (!HasState(EntityInitializationState.TypeMergeMode)) {
                    lazyTypeMergeMode = GetTypeMergeMode() ?? Map.Options.TypeMergeMode;
                    MarkState(EntityInitializationState.TypeMergeMode);
                }
                return lazyTypeMergeMode;
            }
        }

        protected abstract TypeMergeMode? GetTypeMergeMode();

        public bool HasUnifiableBase {
            get {
                if (!HasState(EntityInitializationState.HasUnifiableBase)) {
                    SetFlag(EntityFlags.HasUnifiableBase, GetHasUnifiableBase());
                    MarkState(EntityInitializationState.HasUnifiableBase);
                }
                return GetFlag(EntityFlags.HasUnifiableBase);
            }
        }

        protected abstract bool GetHasUnifiableBase();

        private TypeEntityBase? lazyBase;
        public TypeEntityBase? BaseType {
            get {
                if (!HasState(EntityInitializationState.BaseType)) {
                    lazyBase = GetBaseType();
                    MarkState(EntityInitializationState.BaseType);
                }
                return lazyBase;
            }
        }
        protected abstract TypeEntityBase? GetBaseType();

        public virtual bool IsModuleType => Namespace is null && Name == "<Module>"; // TODO: is there a constant somewhere for this?

        private ImmutableArray<MethodEntityBase> lazyStaticMethods;
        public ImmutableArray<MethodEntityBase> StaticMethods {
            get {
                if (lazyStaticMethods.IsDefault) {
                    ImmutableInterlocked.InterlockedInitialize(
                        ref lazyStaticMethods,
                        MakeStaticMethods()
                    );
                }
                return lazyStaticMethods;
            }
        }

        protected abstract ImmutableArray<MethodEntityBase> MakeStaticMethods();

        private ImmutableArray<MethodEntityBase> lazyInstanceMethods;
        public ImmutableArray<MethodEntityBase> InstanceMethods {
            get {
                if (lazyInstanceMethods.IsDefault) {
                    ImmutableInterlocked.InterlockedInitialize(
                        ref lazyInstanceMethods,
                        MakeInstanceMethods()
                    );
                }
                return lazyInstanceMethods;
            }
        }

        protected abstract ImmutableArray<MethodEntityBase> MakeInstanceMethods();

        private ImmutableArray<TypeEntityBase> lazyNestedTypes;
        public ImmutableArray<TypeEntityBase> NestedTypes {
            get {
                if (lazyNestedTypes.IsDefault) {
                    ImmutableInterlocked.InterlockedInitialize(
                        ref lazyNestedTypes,
                        MakeNestedTypes()
                    );
                }
                return lazyNestedTypes;
            }
        }

        protected abstract ImmutableArray<TypeEntityBase> MakeNestedTypes();
    }
}
