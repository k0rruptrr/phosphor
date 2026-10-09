// GPU-тесты держат свой GL-контекст, замеры времени не должны делить CPU с соседями.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
