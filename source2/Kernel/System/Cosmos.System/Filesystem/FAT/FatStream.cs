﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cosmos.System.Filesystem.FAT {
  public class FatStream : Stream {
    protected readonly Listing.FatFile mFile;
    protected readonly FatFileSystem mFS = null;
    protected byte[] mReadBuffer;
    //TODO: In future we might read this in as needed rather than
    // all at once. This structure will also consume 2% of file size in RAM 
    // (for default cluster size of 2kb, ie 4 bytes per cluster)
    // so we might consider a way to flush it and only keep parts.
    // Example, a 100 MB file will require 2MB for this structure. That is
    // probably acceptable for the mid term future.
    protected List<UInt32> mFatTable;
    protected UInt64? mReadBufferPosition;

    public FatStream(Listing.FatFile aFile) {
      mFile = aFile;
      mFS = mFile.FileSystem;
      mReadBuffer = mFile.FileSystem.NewClusterArray();

      if (mFile.Size > 0) {
        mFatTable = mFile.GetFatTable();
      }
    }

    public override bool CanSeek {
      get { return true; }
    }

    public override bool CanRead {
      get { return true; }
    }

    public override bool CanWrite {
      get { return false; }
    }

    public override long Length {
      get { return (long)mFile.Size; }
    }

    protected UInt64 mPosition;
    public override long Position {
      get {
        return (long)mPosition;
      }
      set {
		if (value < 0L) {
		  throw new ArgumentOutOfRangeException("value");
		}
        mPosition = (ulong)value;
      }
    }

    public override int Read(byte[] aBuffer, int aOffset, int aCount) {
      if (aCount < 0) {
        throw new ArgumentOutOfRangeException("aCount");
	  }
	  else if (aOffset < 0) {
		throw new ArgumentOutOfRangeException("aOffset");
	  }
	  else if(aBuffer == null || aBuffer.Length - aOffset < aCount) {
        throw new ArgumentException("Invalid offset length!");
      } else if (mFile.FirstClusterNum == 0) {
        // FirstSector can be 0 for 0 length files
        return 0;
      } else if (mPosition == mFile.Size) {
        // EOF
        return 0;
      }

	  // reduce count, so that no out of bound exception occurs if not existing
	  // entry is used in line mFS.ReadCluster(mFatTable[(int)xClusterIdx], xCluster);
	  ulong xMaxReadableBytes = mFile.Size - mPosition;
	  ulong xCount = (ulong)aCount;
	  if (xCount > xMaxReadableBytes)
		  xCount = xMaxReadableBytes;

      var xCluster = mFS.NewClusterArray();
      UInt32 xClusterSize = mFS.BytesPerCluster;

      while (xCount > 0) {
        UInt64 xClusterIdx = mPosition / xClusterSize;
        UInt64 xPosInCluster = mPosition % xClusterSize;
        mFS.ReadCluster(mFatTable[(int)xClusterIdx], xCluster);
        long xReadSize;
        if (xPosInCluster + xCount > xClusterSize) {
		  xReadSize = (long)(xClusterSize - xPosInCluster - 1);
        } else {
          xReadSize = (long)xCount;
        }
		// no need for a long version, because internal Array.Copy() does a cast down to int, and a range check,
		// or we do a semantic change here
		Array.Copy(xCluster, (int)xPosInCluster, aBuffer, aOffset, (int)xReadSize);
		//TODO for Kudzu: should aOffset replaced by a local Int64?
        aOffset = (int)(aOffset + xReadSize);
        xCount -= (ulong)xReadSize;
      }

	  mPosition += (ulong)aOffset;
      return aOffset;
	}

    public override void Flush() {
      throw new NotImplementedException();
    }

    public override long Seek(long offset, SeekOrigin origin) {
      throw new NotImplementedException();
    }

    public override void SetLength(long value) {
      throw new NotImplementedException();
    }

    public override void Write(byte[] buffer, int offset, int count) {
      throw new NotImplementedException();
    }
  }
}