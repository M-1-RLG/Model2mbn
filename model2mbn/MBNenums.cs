using System;
using System.Collections.Generic;
using System.Text;

namespace model2mbn
{
	//UV2 wasn't added until later in development that's what's up with the number
	public enum AttributeType : int
	{
		Position = 0,
		Normal = 1,
		Color = 2,
		UV0 = 3,
		UV1 = 4,
		UV2 = 7,
		BoneIndices = 5,
		BoneWeights = 6
	}


	//Forgot all the values...
	public enum DataType : int
	{
		Float,
		Byte,
		SByte,
		SShort,
	}
}
