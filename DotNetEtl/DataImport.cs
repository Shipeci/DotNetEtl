﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetEtl
{
	public class DataImport : IDataImport
	{
		public DataImport(
			IDataSource dataSource,
			IDataDestination dataDestination,
			IRecordValidator recordValidator = null,
			IRecordMapper recordMapper = null,
			IRecordFormatter recordFormatter = null)
			: this(dataSource, new IDataDestination[] { dataDestination }, recordValidator, recordMapper, recordFormatter)
		{
		}

		public DataImport(
			IDataSource dataSource,
			IEnumerable<IDataDestination> dataDestinations,
			IRecordValidator recordValidator = null,
			IRecordMapper recordMapper = null,
			IRecordFormatter recordFormatter = null)
		{
			this.DataSource = dataSource;
			this.DataDestinations = dataDestinations;
			this.RecordValidator = recordValidator;
			this.RecordMapper = recordMapper;
			this.RecordFormatter = recordFormatter;
		}

		public event EventHandler<RecordEvaluatedEventArgs> RecordRead;
		public event EventHandler<RecordEvaluatedEventArgs> RecordValidated;
		public event EventHandler<RecordMappedEventArgs> RecordMapped;
		public event EventHandler<RecordFormattedEventArgs> RecordFormatted;
		public event EventHandler<RecordWrittenEventArgs> RecordWritten;

		public bool CanCommitWithRecordFailures { get; set; }
		public int MaxDegreeOfParallelismForDataWriters { get; set; } = -1;
		public IDataSource DataSource { get; private set; }
		protected IEnumerable<IDataDestination> DataDestinations { get; private set; }
		protected IRecordValidator RecordValidator { get; private set; }
		protected IRecordMapper RecordMapper { get; private set; }
		protected IRecordFormatter RecordFormatter { get; private set; }
		protected CancellationToken CancellationToken { get; private set; }
		protected Dictionary<IDataDestination, IDataWriter> DataWritersDictionary { get; private set; }
		protected IEnumerable<IDataWriter> DataWriters => this.DataWritersDictionary?.Values.AsEnumerable();
		protected List<RecordFailure> RecordFailures { get; private set; }

		public bool TryRun(out IEnumerable<RecordFailure> failures)
		{
			return this.TryRun(CancellationToken.None, out failures);
		}

		public virtual bool TryRun(CancellationToken cancellationToken, out IEnumerable<RecordFailure> failures)
		{
			failures = (this.RecordFailures = new List<RecordFailure>());

			this.CancellationToken = cancellationToken;

			try
			{
				this.PreRun();

				this.DataWritersDictionary = this.CreateDataWriters();

				try
				{
					using (var dataReader = this.DataSource.CreateDataReader())
					{
						dataReader.Open();

						bool couldReadRecord;
						object record;
						IEnumerable<FieldFailure> readRecordFailures;
						var recordIndex = -1;

						do
						{
							if (cancellationToken != CancellationToken.None)
							{
								cancellationToken.ThrowIfCancellationRequested();
							}

							recordIndex++;

							couldReadRecord = this.TryReadRecord(dataReader, out record, out readRecordFailures);

							if (couldReadRecord || readRecordFailures?.Count() > 0)
							{
								this.OnRecordRead(recordIndex, record, couldReadRecord, readRecordFailures);
							}

							if (couldReadRecord)
							{
								this.ProcessRecord(recordIndex, record, out var filteredDataWriters, out var mappedRecord, out var formattedRecord);

								if (filteredDataWriters?.Count() > 0)
								{
									this.WriteRecord(recordIndex, mappedRecord, formattedRecord, filteredDataWriters);
								}
							}
							else if (readRecordFailures?.Count() > 0)
							{
								this.HandleUnreadRecord(recordIndex, readRecordFailures);
							}
						}
						while (couldReadRecord || readRecordFailures?.Count() > 0);
					}

					this.PreCommitOrRollback();

					if (this.RecordFailures.Count == 0 || this.CanCommitWithRecordFailures)
					{
						this.Commit();

						return true;
					}
				}
				catch (Exception exception)
				{
					this.RollbackFromError(exception);

					throw;
				}

				this.Rollback();

				return false;
			}
			finally
			{
				this.Cleanup();
			}
		}

		public void Run()
		{
			if (!this.TryRun(out var failures))
			{
				throw new DataImportFailedException(failures, "Data import failed.");
			}
		}

		public void Run(CancellationToken cancellationToken)
		{
			if (!this.TryRun(cancellationToken, out var failures))
			{
				throw new DataImportFailedException(failures, "Data import failed.");
			}
		}

		protected virtual void PreRun()
		{
		}

		protected virtual void PreCommitOrRollback()
		{
		}

		protected virtual void ProcessRecord(int recordIndex, object record, out IEnumerable<IDataWriter> filteredDataWriters, out object mappedRecord, out object formattedRecord)
		{
			if (this.RecordMapper != null)
			{
				var couldMapRecord = this.TryMapRecord(recordIndex, record, out mappedRecord, out var mappingFailures);

				this.OnRecordMapped(recordIndex, record, couldMapRecord, mappingFailures, mappedRecord);

				if (!couldMapRecord)
				{
					this.HandleUnmappedRecord(recordIndex, mappingFailures);

					formattedRecord = null;
					filteredDataWriters = null;

					return;
				}
			}
			else
			{
				mappedRecord = record;
			}

			if (this.RecordValidator != null)
			{
				var isRecordValid = this.TryValidateRecord(recordIndex, mappedRecord, out var validationFailures);

				this.OnRecordValidated(recordIndex, mappedRecord, isRecordValid, validationFailures);

				if (!isRecordValid)
				{
					this.HandleInvalidatedRecord(recordIndex, validationFailures);

					formattedRecord = null;
					filteredDataWriters = null;

					return;
				}
			}

			filteredDataWriters = this.GetFilteredDataWriters(mappedRecord);

			if (this.RecordFormatter != null)
			{
				formattedRecord = this.FormatRecord(recordIndex, mappedRecord);

				this.OnRecordFormatted(recordIndex, mappedRecord, formattedRecord);
			}
			else
			{
				formattedRecord = mappedRecord;
			}
		}

		protected virtual void HandleUnreadRecord(int recordIndex, IEnumerable<FieldFailure> failures)
		{
			this.RecordFailures.Add(new RecordFailure(recordIndex, failures));
		}

		protected virtual void HandleUnmappedRecord(int recordIndex, IEnumerable<FieldFailure> failures)
		{
			this.RecordFailures.Add(new RecordFailure(recordIndex, failures));
		}

		protected virtual void HandleInvalidatedRecord(int recordIndex, IEnumerable<FieldFailure> failures)
		{
			this.RecordFailures.Add(new RecordFailure(recordIndex, failures));
		}

		protected virtual bool TryReadRecord(IDataReader dataReader, out object record, out IEnumerable<FieldFailure> failures)
		{
			return dataReader.TryReadRecord(out record, out failures);
		}

		protected virtual bool TryMapRecord(int recordIndex, object record, out object mappedRecord, out IEnumerable<FieldFailure> failures)
		{
			return this.RecordMapper.TryMap(record, out mappedRecord, out failures);
		}

		protected virtual bool TryValidateRecord(int recordIndex, object record, out IEnumerable<FieldFailure> failures)
		{
			return this.RecordValidator.TryValidate(record, out failures);
		}

		protected virtual object FormatRecord(int recordIndex, object record)
		{
			return this.RecordFormatter.Format(record);
		}

		protected virtual void WriteRecord(int recordIndex, object record, object formattedRecord, IEnumerable<IDataWriter> dataWriters)
		{
			var parallelOptions = new ParallelOptions()
			{
				MaxDegreeOfParallelism = this.MaxDegreeOfParallelismForDataWriters
			};

			Parallel.ForEach(dataWriters, parallelOptions, dataWriter => dataWriter.WriteRecord(formattedRecord));

			this.OnRecordWritten(recordIndex, record, formattedRecord);
		}

		protected virtual Dictionary<IDataDestination, IDataWriter> CreateDataWriters()
		{
			var dataWriters = new Dictionary<IDataDestination, IDataWriter>();

			foreach (var dataDestination in this.DataDestinations)
			{
				var dataWriter = dataDestination.CreateDataWriter(this.DataSource);

				dataWriter.Open();
				dataWriters.Add(dataDestination, dataWriter);
			}

			return dataWriters;
		}

		protected virtual IEnumerable<IDataWriter> GetFilteredDataWriters(object record)
		{
			var dataWriters = new List<IDataWriter>();
			var filteredDataDestinations = this.DataDestinations.Where(x => x.RecordFilter == null || x.RecordFilter.MeetsCriteria(record));

			foreach (var dataDestination in filteredDataDestinations)
			{
				dataWriters.Add(this.DataWritersDictionary[dataDestination]);
			}

			return dataWriters;
		}

		protected virtual void Commit()
		{
			foreach (var dataWriter in this.DataWriters)
			{
				dataWriter.Commit();
			}
		}

		protected virtual void Rollback()
		{
			var exceptions = new List<Exception>();

			foreach (var dataWriter in this.DataWriters)
			{
				try
				{
					dataWriter.Rollback();
				}
				catch (Exception exception)
				{
					exceptions.Add(exception);
				}
			}

			if (exceptions.Count > 0)
			{
				throw new AggregateException("One or more data writers failed to rollback.", exceptions);
			}
		}

		private void RollbackFromError(Exception exception)
		{
			try
			{
				this.Rollback();
			}
			catch (Exception rollbackException)
			{
				throw new AggregateException(exception, rollbackException);
			}
		}

		protected virtual void Cleanup()
		{
			var dataWriters = this.DataWriters;

			if (dataWriters != null)
			{
				foreach (var dataWriter in dataWriters)
				{
					dataWriter.Dispose();
				}
			}
		}

		protected virtual void OnRecordRead(int recordIndex, object record, bool wasSuccessful, IEnumerable<FieldFailure> failures)
		{
			this.RecordRead?.Invoke(this, new RecordEvaluatedEventArgs(recordIndex, record, wasSuccessful, failures));
		}

		protected virtual void OnRecordMapped(int recordIndex, object record, bool wasSuccessful, IEnumerable<FieldFailure> failures, object mappedRecord)
		{
			this.RecordMapped?.Invoke(this, new RecordMappedEventArgs(recordIndex, record, wasSuccessful, failures, mappedRecord));
		}

		protected virtual void OnRecordValidated(int recordIndex, object record, bool wasSuccessful, IEnumerable<FieldFailure> failures)
		{
			this.RecordValidated?.Invoke(this, new RecordEvaluatedEventArgs(recordIndex, record, wasSuccessful, failures));
		}

		protected virtual void OnRecordFormatted(int recordIndex, object record, object formattedRecord)
		{
			this.RecordFormatted?.Invoke(this, new RecordFormattedEventArgs(recordIndex, record, formattedRecord));
		}

		protected virtual void OnRecordWritten(int recordIndex, object record, object formattedRecord)
		{
			this.RecordWritten?.Invoke(this, new RecordWrittenEventArgs(recordIndex, record, formattedRecord));
		}
	}
}
