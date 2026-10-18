# `.fh6rec` format, version 1

A recording is a header followed by records until end of file. All integers are **little-endian**.

```
HEADER (24 bytes)
  offset size  field
  0      6     magic "FH6REC" (ASCII)
  6      2     format version (uint16) = 1
  8      8     recording start, UTC ticks (int64, 100 ns since 0001-01-01)
  16     4     flags (uint32), reserved, 0
  20     4     reserved (uint32), 0

RECORD (repeated)
  offset size  field
  0      8     elapsed since start, microseconds (int64, never decreasing)
  8      4     payload length N (int32, 0..65535)
  12     N     payload = the raw UDP datagram (324 bytes for FH6 today)
```

## Decisions

- **Raw datagrams**: recordings can be re-parsed after a parser change or bug fix. Wrong-size datagrams are
  stored too; replay applies the same "skip what does not parse" rule as the live listener.
- **Microseconds**: finer than 1 ms so 60 Hz intervals do not quantize, coarser than 100 ns ticks (false
  precision for socket arrival). int64 microseconds is good for ~292,000 years.
- **Elapsed, not absolute time**: record times are relative to the header start, so a recording is
  independent of the wall clock and the header alone says when it happened.
- **Fixed-size header + version**: a reader refuses a newer version explicitly instead of misreading it.
  Future versions may append data but must keep the first 8 bytes (magic + version) so every reader can tell.
- **Crash tolerance**: the writer buffers, so a crash can leave a half-written last record. Readers stop
  cleanly at the last complete record and report `IsTruncated`.
- **Sanity limit**: a length above 65,535 (the UDP maximum) is treated as corruption, not allocated.

## Evolution rules

1. Never change the meaning of an existing field within a version.
2. A change that old readers cannot ignore bumps the version.
3. Flags/reserved bits are 0 today; readers must ignore bits they do not know.
