using System.Runtime.CompilerServices;

// Guardrail: no runtime marshalling anywhere. Interop is blittable structs and
// function pointers only, so JIT dev builds and NativeAOT publish builds share
// identical interop behavior.
[assembly: DisableRuntimeMarshalling]
