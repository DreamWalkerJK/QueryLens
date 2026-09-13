-- DBA review only; never run automatically.
GRANT SELECT ON performance_schema.events_statements_summary_by_digest TO 'querylens_reader'@'%';
