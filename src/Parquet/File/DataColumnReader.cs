﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Parquet.Data;
using Parquet.File.Data;
using Parquet.File.Streams;
using Parquet.File.Values;

namespace Parquet.File
{
   // v3 experimental !!!
   class DataColumnReader
   {
      private readonly DataField _dataField;
      private readonly Stream _inputStream;
      private readonly Thrift.ColumnChunk _thriftColumnChunk;
      private readonly Thrift.SchemaElement _thriftSchemaElement;
      private readonly ThriftFooter _footer;
      private readonly ParquetOptions _parquetOptions;
      private readonly ThriftStream _thriftStream;
      private readonly int _maxRepetitionLevel;
      private readonly int _maxDefinitionLevel;
      private readonly IDataTypeHandler _dataTypeHandler;

      private class ColumnRawData
      {
         public int maxCount;

         public int[] repetitions;
         public int repetitionsOffset;

         public int[] definitions;
         public int definitionsOffset;

         public int[] indexes;
         public int indexesOffset;

         public Array values;
         public int valuesOffset;

         public Array dictionary;
         public int dictionaryOffset;
      }

      public DataColumnReader(
         DataField dataField,
         Stream inputStream,
         Thrift.ColumnChunk thriftColumnChunk,
         ThriftFooter footer,
         ParquetOptions parquetOptions)
      {
         _dataField = dataField ?? throw new ArgumentNullException(nameof(dataField));
         _inputStream = inputStream ?? throw new ArgumentNullException(nameof(inputStream));
         _thriftColumnChunk = thriftColumnChunk ?? throw new ArgumentNullException(nameof(thriftColumnChunk));
         _footer = footer ?? throw new ArgumentNullException(nameof(footer));
         _parquetOptions = parquetOptions ?? throw new ArgumentNullException(nameof(parquetOptions));

         _thriftStream = new ThriftStream(inputStream);
         _footer.GetLevels(_thriftColumnChunk, out int mrl, out int mdl);
         _maxRepetitionLevel = mrl;
         _maxDefinitionLevel = mdl;
         _thriftSchemaElement = _footer.GetSchemaElement(_thriftColumnChunk);
         _dataTypeHandler = DataTypeFactory.Match(_thriftSchemaElement, _parquetOptions);
      }

      public DataColumn Read()
      {
         long fileOffset = GetFileOffset();
         long maxValues = _thriftColumnChunk.Meta_data.Num_values;

         _inputStream.Seek(fileOffset, SeekOrigin.Begin);

         var colData = new ColumnRawData();
         colData.maxCount = (int)_thriftColumnChunk.Meta_data.Num_values;

         //there can be only one dictionary page in column
         Thrift.PageHeader ph = _thriftStream.Read<Thrift.PageHeader>();
         if (TryReadDictionaryPage(ph, out colData.dictionary, out colData.dictionaryOffset))
         {
            ph = _thriftStream.Read<Thrift.PageHeader>();
         }

         int pagesRead = 0;

         while (true)
         {
            int valuesSoFar = Math.Max(colData.indexes == null ? 0 : colData.indexes.Length, colData.values == null ? 0 : colData.values.Length);
            ReadDataPage(ph, colData, maxValues - valuesSoFar);

            pagesRead++;

            int totalCount = Math.Max(
               (colData.values == null ? 0 : colData.values.Length) +
               (colData.indexes == null ? 0 : colData.indexes.Length),
               (colData.definitions == null ? 0 : colData.definitions.Length));
            if (totalCount >= maxValues) break; //limit reached

            ph = _thriftStream.Read<Thrift.PageHeader>();
            if (ph.Type != Thrift.PageType.DATA_PAGE) break;
         }

         // all the data is available here!

         // todo: this is a simple hack for trivial tests to succeed

         return new DataColumn(
            _dataField, colData.values,
            colData.definitions, _maxDefinitionLevel,
            colData.repetitions, _maxRepetitionLevel,
            colData.dictionary);
      }

      private static void MergeDictionary(ColumnRawData cd)
      {
         if (cd.indexes == null) return;

         int[] tv = (int[])cd.values;

         for(int i = 0; i < cd.valuesOffset; i++)
         {
            int index = tv[i];
            cd.values.SetValue(cd.dictionary.GetValue(index), i);
         }

         cd.indexes = null;
         cd.indexesOffset = 0;
      }

      private bool TryReadDictionaryPage(Thrift.PageHeader ph, out Array dictionary, out int dictionaryOffset)
      {
         if (ph.Type != Thrift.PageType.DICTIONARY_PAGE)
         {
            dictionary = null;
            dictionaryOffset = 0;
            return false;
         }

         //Dictionary page format: the entries in the dictionary - in dictionary order - using the plain encoding.

         using (Stream pageStream = OpenDataPageStream(ph))
         {
            using (var dataReader = new BinaryReader(pageStream))
            {
               dictionary = Array.CreateInstance(
                  _dataField.ClrType,
                  (int)_thriftColumnChunk.Meta_data.Num_values);

               dictionaryOffset = _dataTypeHandler.Read(dataReader, _thriftSchemaElement, dictionary, 0, _parquetOptions);

               return true;
            }
         }
      }

      private Stream OpenDataPageStream(Thrift.PageHeader pageHeader)
      {
         var window = new WindowedStream(_inputStream, pageHeader.Compressed_page_size);

         Stream uncompressed = DataStreamFactory.CreateReader(window, _thriftColumnChunk.Meta_data.Codec, pageHeader.Uncompressed_page_size);

         return uncompressed;
      }

      private long GetFileOffset()
      {
         //get the minimum offset, we'll just read pages in sequence

         return
            new[]
            {
               _thriftColumnChunk.Meta_data.Dictionary_page_offset,
               _thriftColumnChunk.Meta_data.Data_page_offset
            }
            .Where(e => e != 0)
            .Min();
      }

      private void ReadDataPage(Thrift.PageHeader ph, ColumnRawData cd, long maxValues)
      {
         int max = ph.Data_page_header.Num_values;

         using (Stream pageStream = OpenDataPageStream(ph))
         {
            using (var reader = new BinaryReader(pageStream))
            {
               if (_maxRepetitionLevel > 0)
               {
                  if (cd.repetitions == null) cd.repetitions = new int[cd.maxCount];

                  cd.repetitionsOffset += ReadLevels(reader, _maxRepetitionLevel, max, cd.repetitions, cd.repetitionsOffset);
               }

               if (_maxDefinitionLevel > 0)
               {
                  if (cd.definitions == null) cd.definitions = new int[cd.maxCount];

                  cd.definitionsOffset += ReadLevels(reader, _maxDefinitionLevel, max, cd.definitions, cd.definitionsOffset);
               }

               ReadColumn(reader, ph.Data_page_header.Encoding, maxValues,
                  max,
                  ref cd.values, ref cd.valuesOffset,
                  ref cd.indexes, ref cd.indexesOffset);
            }
         }
      }

      private int ReadLevels(BinaryReader reader, int maxLevel, int maxValues, int[] dest, int offset)
      {
         int bitWidth = maxLevel.GetBitWidth();

         return RunLengthBitPackingHybridValuesReader.ReadRleBitpackedHybrid(reader, bitWidth, 0, dest, offset);
      }

      private void ReadColumn(BinaryReader reader, Thrift.Encoding encoding, long maxValues,
         int maxArrayLength,
         ref Array values, ref int valuesOffset,
         ref int[] indexes, ref int indexesOffset)
      {
         //dictionary encoding uses RLE to encode data

         switch (encoding)
         {
            case Thrift.Encoding.PLAIN:
               if (values == null) values = Array.CreateInstance(_dataField.ClrType, maxArrayLength);
               valuesOffset += _dataTypeHandler.Read(reader, _thriftSchemaElement, values, valuesOffset, _parquetOptions);
               break;

            case Thrift.Encoding.RLE:
               if (indexes == null) indexes = new int[maxArrayLength];
               indexesOffset += RunLengthBitPackingHybridValuesReader.Read(reader, _thriftSchemaElement.Type_length, indexes, indexesOffset);
               break;

            case Thrift.Encoding.PLAIN_DICTIONARY:
               if (indexes == null) indexes = new int[maxArrayLength];
               indexesOffset += ReadPlainDictionary(reader, maxValues, indexes, indexesOffset);
               break;

            default:
               throw new ParquetException($"encoding {encoding} is not supported.");
         }
      }

      private static int ReadPlainDictionary(BinaryReader reader, long maxValues, int[] dest, int offset)
      {
         int start = offset;
         int bitWidth = reader.ReadByte();

         //when bit width is zero reader must stop and just repeat zero maxValue number of times
         if (bitWidth == 0)
         {
            for (int i = 0; i < maxValues; i++)
            {
               dest[offset++] = 0;
            }
         }
         else
         {
            int length = GetRemainingLength(reader);
            offset += RunLengthBitPackingHybridValuesReader.ReadRleBitpackedHybrid(reader, bitWidth, length, dest, offset);
         }

         return offset - start;
      }

      private static int GetRemainingLength(BinaryReader reader)
      {
         return (int)(reader.BaseStream.Length - reader.BaseStream.Position);
      }

      #region [ To be culled ]

      private List<int> AssignOrAdd(List<int> container, List<int> source)
      {
         if (source != null)
         {
            if (container == null)
            {
               container = source;
            }
            else
            {
               container.AddRange(source);
            }
         }

         return container;
      }

      private IList AssignOrAdd(IList container, IList source)
      {
         if (source != null)
         {
            if (container == null)
            {
               container = source;
            }
            else
            {
               foreach (object item in source)
               {
                  container.Add(item);
               }
            }
         }

         return container;
      }


      #endregion
   }
}
