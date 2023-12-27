# MedianSketch
This is the source code for the [Median Sketch plugin for Paint.NET](https://forums.getpaint.net/topic/124261-median-sketch-gpu/). 

This uses the P² Quantile Estimator algorithm as described in these 4 blog posts, adapted for running as a pixel shader on the GPU:
- [Part 1: P² quantile estimator: estimating the median without storing values](https://aakinshin.net/posts/p2-quantile-estimator/)
- [Part 2: P² quantile estimator rounding issue](https://aakinshin.net/posts/p2-quantile-estimator-rounding-issue/)
- [Part 3: P² quantile estimator initialization strategy](https://aakinshin.net/posts/p2-quantile-estimator-initialization/)
- [Part 4: P² quantile estimator marker adjusting order](https://aakinshin.net/posts/p2-quantile-estimator-adjusting-order/)
