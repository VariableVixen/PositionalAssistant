using System;

namespace VariableVixen.PositionalGuide;

[Flags]
public enum DrawGuides: ushort {
	None = 0,
	Front = 1,
	Right = 2,
	Back = 4,
	Left = 8,
	FrontRight = 16,
	BackRight = 32,
	BackLeft = 64,
	FrontLeft = 128,
	Circle = 256,

	Cardinals = Front | Right | Back | Left,
	Diagonals = FrontLeft | FrontRight | BackRight | BackLeft,
	Lines = Cardinals | Diagonals,
	All = Lines | Circle,
}
