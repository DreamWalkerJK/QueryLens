-- DBA review only; run in the appropriate database / instance security context.
GRANT VIEW DATABASE STATE TO [querylens_reader];
-- DMV collection may additionally require: GRANT VIEW SERVER STATE TO [querylens_reader];
-- SHOWPLAN is needed only for an explicit, isolated plan collection action.
