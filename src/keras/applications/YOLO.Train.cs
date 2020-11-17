namespace tensorflow.keras.applications {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;

    using LostTech.Gradient;
    using LostTech.Gradient.BuiltIns;
    using LostTech.Gradient.ManualWrappers;

    using numpy;

    using tensorflow.data;
    using tensorflow.errors;
    using tensorflow.keras.callbacks;
    using tensorflow.keras.models;
    using tensorflow.keras.optimizers;
    using tensorflow.python.eager.context;
    using tensorflow.python.ops.summary_ops_v2;

    public static partial class YOLO {
        public static void Train(Model model, IOptimizer optimizer, ObjectDetectionDataset dataset,
                                 ObjectDetectionDataset? testSet = null,
                                 IEnumerable<ICallback>? callbacks = null,
                                 int batchSize = 2,
                                 int warmupEpochs = 2, int firstStageEpochs = 20,
                                 int secondStageEpochs = 30,
                                 float initialLearningRate = 1e-3f,
                                 float finalLearningRate = 1e-6f,
                                 bool testRun = false) {
            var globalSteps = new Variable(1, dtype: tf.int64);

            var learningRateSchedule = new YOLO.LearningRateSchedule(
                totalSteps: (long)(firstStageEpochs + secondStageEpochs) * dataset.BatchCount(batchSize),
                warmupSteps: warmupEpochs * dataset.BatchCount(batchSize),
                initialLearningRate: initialLearningRate,
                finalLearningRate: finalLearningRate);

            foreach (var callback in callbacks ?? Array.Empty<ICallback>()) {
                callback.DynamicInvoke<object>("set_model", model);
                callback.DynamicInvoke<object>("set_params", new Dictionary<string, object> {
                    ["metrics"] = new[] { "loss", "testLoss" }.SelectMany(prefix => new[]{
                        prefix + nameof(Loss.GIUO),
                        prefix + nameof(Loss.Conf),
                        prefix + nameof(Loss.Prob),
                    }).ToArray(),
                });
            }

            bool isFreeze = false;
            // see https://github.com/hunglc007/tensorflow-yolov4-tflite/commit/9ab36aaa90c46aa063e3356d8e7f0e5bb27d919b
            string[] freezeLayers = { "conv2d_93", "conv2d_101", "conv2d_109" };
            void SetFreeze(bool freeze) {
                foreach(string name in freezeLayers) {
                    var layer = model.get_layer(name);
                    Utils.SetTrainableRecursive(layer, !freeze);
                }
            }
            int totalBatches = 0;
            foreach (int epoch in Enumerable.Range(0, firstStageEpochs + secondStageEpochs)) {
                    if (epoch < firstStageEpochs) {
                        if (!isFreeze) {
                            isFreeze = true;

                            SetFreeze(true);
                        }
                    } else {
                        if (isFreeze) {
                            isFreeze = false;
                            SetFreeze(false);
                        }
                    }

                foreach (var callback in callbacks ?? Array.Empty<ICallback>())
                    callback.on_epoch_begin(epoch);

                var trainLoss = new FinalLoss();
                int allocIssues = 0;
                foreach (var batch in dataset.Batch(batchSize: batchSize,
                                                    onloadAugmentation: ObjectDetectionDataset.RandomlyApplyAugmentations)
                                      .BufferedEnumerate(bufferSize: 6)) {
                    // TODO: https://github.com/hunglc007/tensorflow-yolov4-tflite/commit/9ab36aaa90c46aa063e3356d8e7f0e5bb27d919b
                    try {
                        var stepLoss = TrainStep(batch, model, optimizer, dataset.ClassNames.Length, dataset.Strides);
                        trainLoss += stepLoss.AsFinal();

                        int reportSteps = testRun ? dataset.BatchCount(batchSize) : 1;
                        globalSteps.assign_add_dyn(reportSteps);
                        totalBatches += reportSteps;

                        UpdateLearningRate(optimizer, globalSteps, learningRateSchedule);

                        WriteLosses(optimizer, globalSteps, stepLoss);
                        summary_ops_v2.scalar("epoch", epoch, step: globalSteps);

                        stepLoss = default;

                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        allocIssues = 0;

                        if (testRun)
                            break;
                    } catch (ResourceExhaustedError e) {
                        allocIssues++;
                        Trace.TraceError(e.ToString());
                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        if (allocIssues > 10) {
                            throw;
                        }
                    }
                }

                var testLoss = new FinalLoss();
                if (testSet != null) {
                    foreach (var batch in testSet.Batch(batchSize: batchSize, onloadAugmentation: null))
                        try {
                            testLoss += TestStep(batch, model, dataset.ClassNames.Length, dataset.Strides).AsFinal();

                            GC.Collect();
                            GC.WaitForPendingFinalizers();

                            allocIssues = 0;
                            if (testRun)
                                break;
                        } catch (ResourceExhaustedError e) {
                            allocIssues++;
                            Trace.TraceError(e.ToString());
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            if (allocIssues > 10) {
                                throw;
                            }
                        }
                }

                foreach (var callback in callbacks ?? Array.Empty<ICallback>()) {
                    var logs = new Dictionary<string, object?>();
                    (trainLoss / dataset.BatchCount(batchSize)).Write(logs, "loss");
                    if (testSet != null)
                        (testLoss / testSet.Count).Write(logs, "testLoss");
                    callback.on_epoch_end(epoch, logs: logs);
                }
            }
        }

        static Loss TestStep(ObjectDetectionDataset.EntryBatch batch, Model model, int classCount, ReadOnlySpan<int> strides) {
            return ComputeLosses(model, batch, classCount, strides);
        }

        static Loss TrainStep(ObjectDetectionDataset.EntryBatch batch, Model model, IOptimizer optimizer, int classCount, ReadOnlySpan<int> strides) {
            var tape = new GradientTape();
            Loss losses;
            Tensor totalLoss;
            using (tape.StartUsing()) {
                losses = ComputeLosses(model, batch, classCount, strides);
                totalLoss = losses.GIUO + losses.Conf + losses.Prob;

                if (!tf.executing_eagerly() || !tf.logical_or(tf.is_inf(totalLoss), tf.is_nan(totalLoss)).numpy().any()) {
                    PythonList<Tensor> gradients = tape.gradient(totalLoss, model.trainable_variables);
                    optimizer.apply_gradients(gradients.Zip(
                        (PythonList<Variable>)model.trainable_variables, (g, v) => (g, v)));
                } else {
                    Trace.TraceWarning("NaN/inf loss ignored");
                }
            }

            return losses;
        }

        static void WriteLosses(IOptimizer optimizer, Variable globalSteps, Loss losses) {
            // tf v1 does not actually export summary.experimental.set_step
            context.context_().summary_step = globalSteps;

            void Scalar(string name, IGraphNodeBase value)
                => summary_ops_v2.scalar(name, value, step: globalSteps);

            Scalar("lr", optimizer.DynamicGet<Variable>("lr"));
            Scalar("loss/total_loss", losses.GIUO + losses.Conf + losses.Prob);
            Scalar("loss/giou_loss", losses.GIUO);
            Scalar("loss/conf_loss", losses.Conf);
            Scalar("loss/prob_loss", losses.Prob);
        }

        static void UpdateLearningRate(IOptimizer optimizer, Variable step, LearningRateSchedule learningRateSchedule) {
            var learningRate = learningRateSchedule.Get(step: step);
            var optimizerLearningRate = optimizer.DynamicGet<Variable>("lr");
            optimizerLearningRate.assign_dyn(learningRate);
        }

        public static Loss ComputeLosses(Model model,
                                         ObjectDetectionDataset.EntryBatch batch,
                                         int classCount, ReadOnlySpan<int> strides) {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (classCount <= 0) throw new ArgumentOutOfRangeException(nameof(classCount));

            var output = model.__call___dyn(batch.Images, new { training = true }.AsKwArgs());
            var loss = Loss.Zero;
            for (int scaleIndex = 0; scaleIndex < YOLOv4.XYScale.Length; scaleIndex++) {
                Tensor conv = output[scaleIndex * 2];
                Tensor pred = output[scaleIndex * 2 + 1];

                loss += ComputeLoss(pred, conv,
                                    targetLabels: batch.BBoxLabels[scaleIndex],
                                    targetBBoxes: batch.BBoxes[scaleIndex],
                                    strideSize: strides[scaleIndex],
                                    classCount: classCount,
                                    intersectionOverUnionLossThreshold: DefaultIntersectionOverUnionLossThreshold);
            }

            return loss;
        }

        public static Model CreateV4Trainable(int inputSize, int classCount, ReadOnlySpan<int> strides) {
            if (inputSize <= 0) throw new ArgumentOutOfRangeException(nameof(inputSize));
            if (classCount <= 0) throw new ArgumentOutOfRangeException(nameof(classCount));

            Tensor input = tf.keras.Input(new TensorShape(inputSize, inputSize, 3));
            var featureMaps = YOLOv4.Apply(input, classCount: classCount);

            var bboxTensors = new PythonList<Tensor>();
            foreach (var (scaleIndex, featureMap) in Tools.Enumerate(featureMaps.SSBox, featureMaps.MBBox, featureMaps.LBBox)) {
                var bbox = DecodeTrain(featureMap, classCount: classCount,
                    anchors: YOLOv4.Anchors, strides: strides,
                    scaleIndex: scaleIndex, xyScale: YOLOv4.XYScale);
                bboxTensors.Add(featureMap);
                bboxTensors.Add(bbox);
            }
            return new Model(new { inputs = input, outputs = bboxTensors }.AsKwArgs());
        }

        public struct Loss {
            public Tensor GIUO { get; set; }
            public Tensor Conf { get; set; }
            public Tensor Prob { get; set; }

            public FinalLoss AsFinal() => new FinalLoss {
                GIUO = this.GIUO.numpy().AsScalar<float>(),
                Conf = this.Conf.numpy().AsScalar<float>(),
                Prob = this.Prob.numpy().AsScalar<float>(),
            };

            public static Loss operator +(Loss a, Loss b) => new Loss {
                GIUO = a.GIUO + b.GIUO,
                Conf = a.Conf + b.Conf,
                Prob = a.Prob + b.Prob,
            };

            public static Loss Zero {
                get {
                    var zero = tf.constant(0f);
                    return new Loss {
                        GIUO = zero,
                        Conf = zero,
                        Prob = zero,
                    };
                }
            }
        }

        public struct FinalLoss {
            public float GIUO { get; set; }
            public float Conf { get; set; }
            public float Prob { get; set; }

            public static FinalLoss operator +(FinalLoss a, FinalLoss b) => new FinalLoss {
                GIUO = a.GIUO + b.GIUO,
                Conf = a.Conf + b.Conf,
                Prob = a.Prob + b.Prob,
            };

            public static FinalLoss operator /(FinalLoss a, float divisor) => new FinalLoss {
                GIUO = a.GIUO / divisor,
                Conf = a.Conf / divisor,
                Prob = a.Prob / divisor,
            };

            public void Write(IDictionary<string, object?> metrics, string prefix = "") {
                if (metrics is null) throw new ArgumentNullException(nameof(metrics));
                if (prefix is null) throw new ArgumentNullException(nameof(prefix));

                metrics[prefix + nameof(this.GIUO)] = this.GIUO;
                metrics[prefix + nameof(this.Conf)] = this.Conf;
                metrics[prefix + nameof(this.Prob)] = this.Prob;
            }
        }

        static Loss ComputeLoss(Tensor pred, Tensor conv,
                                ndarray<float> targetLabels, ndarray<float> targetBBoxes,
                                int strideSize, int classCount,
                                float intersectionOverUnionLossThreshold) {
            Tensor batchSize = tf.shape(conv)[0];
            Tensor outputSize = tf.shape(conv)[1];
            Tensor inputSize = strideSize * outputSize;

            conv = tf.reshape_dyn(conv, new object[] { batchSize, outputSize, outputSize, 3, 5 + classCount });

            var convRawConf = conv[.., .., .., .., 4..5];
            var convRawProb = conv[.., .., .., .., 5..];

            var predXYWH = pred[.., .., .., .., 0..4];
            var predConf = pred[.., .., .., .., 4..5];

            var labelXYWH = AsTensor(targetLabels[.., .., .., .., 0..4]);
            var respondBBox = AsTensor(targetLabels[.., .., .., .., 4..5]);
            var labelProb = AsTensor(targetLabels[.., .., .., .., 5..]);

            var generalizedIntersectionOverUnion = tf.expand_dims(
                BBoxGeneralizedIntersectionOverUnion(predXYWH, labelXYWH),
                axis: - 1);

            Tensor bboxLossScale = 2f - 1f * labelXYWH[.., .., .., .., 2..3] * labelXYWH[.., .., .., .., 3..4] / tf.cast(inputSize * inputSize, tf.float32);
            Tensor generalizedIntersectionOverUnionLoss =
                respondBBox * bboxLossScale * (1 - generalizedIntersectionOverUnion);

            var intersectionOverUnion = BBoxIOU(
                boxes1: predXYWH[.., .., .., .., tf.newaxis, ..],
                boxes2: AsTensor(targetBBoxes[.., np.newaxis, np.newaxis, np.newaxis, .., ..]));

            var maxIntersectionOverUnion = tf.expand_dims(
                tf.reduce_max(intersectionOverUnion, axis: new[] { -1 }),
                axis: -1);

            var respondBackground = (1f - respondBBox) * tf.cast(maxIntersectionOverUnion < intersectionOverUnionLossThreshold, tf.float32);

            Tensor confFocal = tf.pow(respondBBox - predConf, 2);
            Tensor confLoss = confFocal * (
                respondBBox * tf.nn.sigmoid_cross_entropy_with_logits(labels: respondBBox, logits: convRawConf)
                +
                respondBackground * tf.nn.sigmoid_cross_entropy_with_logits(labels: respondBBox, logits: convRawConf));

            Tensor probLoss = respondBBox * tf.nn.sigmoid_cross_entropy_with_logits(labels: labelProb, logits: convRawProb);

            generalizedIntersectionOverUnionLoss = tf.reduce_mean(tf.reduce_sum(generalizedIntersectionOverUnionLoss, axis: new[] { 1, 2, 3, 4 }));
            confLoss = tf.reduce_mean(tf.reduce_sum(confLoss, axis: new[] { 1, 2, 3, 4 }));
            probLoss = tf.reduce_mean(tf.reduce_sum(probLoss, axis: new[] { 1, 2, 3, 4 }));

            return new Loss {
                GIUO = generalizedIntersectionOverUnionLoss,
                Conf = confLoss,
                Prob = probLoss,
            };
        }

        // workaround for a GIL-related bug in tf.constant_scalar
        static Tensor<T> AsTensor<T>(IArrayLike<T> numpyValue) {
            using var _ = Python.Runtime.Py.GIL();
            return tf.constant_scalar<T>(default(T)).DynamicInvoke<Tensor<T>>("__add__", numpyValue);
        }

        // TODO: https://github.com/hunglc007/tensorflow-yolov4-tflite/commit/a689606a5a75b22e2363796b996d964cf2c47e77
        static Tensor BBoxIOU(Tensor boxes1, Tensor boxes2) {
            var area1 = boxes1[tf.rest_of_the_axes, 2] * boxes1[tf.rest_of_the_axes, 3];
            var area2 = boxes1[tf.rest_of_the_axes, 2] * boxes1[tf.rest_of_the_axes, 3];

            boxes1 = tf.concat(new[] {
                boxes1[tf.rest_of_the_axes, ..2] - boxes1[tf.rest_of_the_axes, 2..] * 0.5f,
                boxes1[tf.rest_of_the_axes, ..2] + boxes1[tf.rest_of_the_axes, 2..] * 0.5f,
            }, axis: -1);
            boxes2 = tf.concat(new[] {
                boxes2[tf.rest_of_the_axes, ..2] - boxes2[tf.rest_of_the_axes, 2..]*0.5f,
                boxes2[tf.rest_of_the_axes, ..2] + boxes2[tf.rest_of_the_axes, 2..]*0.5f,
            }, axis: -1);

            Tensor leftUp = tf.maximum(boxes1[tf.rest_of_the_axes, ..2], boxes2[tf.rest_of_the_axes, ..2]);
            Tensor rightDown = tf.minimum(boxes1[tf.rest_of_the_axes, 2..], boxes2[tf.rest_of_the_axes, 2..]);

            var intersection = tf.maximum(rightDown - leftUp, 0.0f);
            var intersectionArea = intersection[tf.rest_of_the_axes, 0] * intersection[tf.rest_of_the_axes, 1];
            var unionArea = tf.maximum(area1 + area2 - intersectionArea,
                                       tf.keras.backend.epsilon());

            return tf.maximum(tf.keras.backend.epsilon(), intersectionArea / unionArea);
        }
        // TODO: https://github.com/hunglc007/tensorflow-yolov4-tflite/commit/7b3814db72dc3775eda7136186e71ea2c0e777eb
        static Tensor BBoxGeneralizedIntersectionOverUnion(Tensor boxes1, Tensor boxes2) {
            boxes1 = tf.concat(new[]{
                boxes1[tf.rest_of_the_axes, ..2] - boxes1[tf.rest_of_the_axes, 2..] * 0.5f,
                boxes1[tf.rest_of_the_axes, ..2] + boxes1[tf.rest_of_the_axes, ..2] * 0.5f,
                }, axis: -1);
            boxes2 = tf.concat(new[]{
                boxes2[tf.rest_of_the_axes, ..2] - boxes2[tf.rest_of_the_axes, 2..] * 0.5f,
                boxes2[tf.rest_of_the_axes, ..2] + boxes2[tf.rest_of_the_axes, ..2] * 0.5f,
                }, axis: -1);

            boxes1 = tf.concat(new[] {
                tf.minimum(boxes1[tf.rest_of_the_axes, ..2], boxes1[tf.rest_of_the_axes, 2..]),
                tf.maximum(boxes1[tf.rest_of_the_axes, ..2], boxes1[tf.rest_of_the_axes, 2..]),
            }, axis: -1);
            boxes2 = tf.concat(new[] {
                tf.minimum(boxes2[tf.rest_of_the_axes, ..2], boxes2[tf.rest_of_the_axes, 2..]),
                tf.maximum(boxes2[tf.rest_of_the_axes, ..2], boxes2[tf.rest_of_the_axes, 2..]),
            }, axis: -1);

            var boxes1Area = BoxesArea(boxes1);
            var boxes2Area = BoxesArea(boxes2);

            Tensor leftUp = tf.maximum(boxes1[tf.rest_of_the_axes, ..2], boxes2[tf.rest_of_the_axes, ..2]);
            Tensor rigthDown = tf.minimum(boxes1[tf.rest_of_the_axes, 2..], boxes2[tf.rest_of_the_axes, 2..]);

            Tensor intersection = tf.maximum(rigthDown - leftUp, 0);
            Tensor intersectionArea = intersection[tf.rest_of_the_axes, 0] * intersection[tf.rest_of_the_axes, 1];
            Tensor unionArea = tf.maximum(boxes1Area + boxes2Area - intersectionArea,
                                          tf.keras.backend.epsilon());
            Tensor intersectionOverUnion = intersectionArea / unionArea;

            Tensor encloseLeftUp = tf.minimum(boxes1[tf.rest_of_the_axes, ..2], boxes2[tf.rest_of_the_axes, ..2]);
            Tensor encloseRightDown = tf.maximum(boxes1[tf.rest_of_the_axes, 2..], boxes2[tf.rest_of_the_axes, 2..]);
            Tensor enclose = tf.maximum(encloseRightDown - encloseLeftUp, 0);
            Tensor encloseArea = tf.maximum(enclose[tf.rest_of_the_axes, 0] * enclose[tf.rest_of_the_axes, 1],
                                            tf.keras.backend.epsilon());

            var generalized = intersectionOverUnion - 1f * (encloseArea - unionArea) / encloseArea;
            return tf.maximum(tf.keras.backend.epsilon(), generalized);
        }

        static Tensor BoxesArea(Tensor boxes)
            => (boxes[tf.rest_of_the_axes, 2] - boxes[tf.rest_of_the_axes, 0])
                * (boxes[tf.rest_of_the_axes, 3] - boxes[tf.rest_of_the_axes, 1]);

        static Tensor DecodeTrain(Tensor convOut, int classCount,
                                  ReadOnlySpan<int> strides, ReadOnlySpan<int> anchors,
                                  int scaleIndex, ReadOnlySpan<float> xyScale) {
            var varScope = new variable_scope("scale" + scaleIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var _ = varScope.StartUsing();
            Tensor batchSize = tf.shape(convOut)[0];
            Tensor outputSize = tf.shape(convOut)[1];

            convOut = tf.reshape_dyn(convOut, new object[] { batchSize, outputSize, outputSize, 3, 5 + classCount });
            Tensor[] raws = tf.split(convOut, new[] { 2, 2, 1, classCount }, axis: -1);
            var (convRawDxDy, convRawDwDh, convRawConf, convRawProb) = raws;

            var meshgrid = tf.meshgrid(tf.range_dyn(outputSize), tf.range_dyn(outputSize));
            meshgrid = tf.expand_dims(tf.stack(meshgrid, axis: -1), axis: 2); // [gx, gy, 1, 2]
            Tensor xyGrid = tf.tile_dyn(
                tf.expand_dims(meshgrid, axis: 0),
                new object[] { tf.shape(convOut)[0], 1, 1, 3, 1 });

            xyGrid = tf.cast(xyGrid, tf.float32);

            var predictedXY = ((tf.sigmoid(convRawDxDy) * xyScale[scaleIndex]) - 0.5 * (xyScale[scaleIndex] - 1) + xyGrid) * strides[scaleIndex];
            var predictedWH = tf.exp(convRawDwDh) * anchors[scaleIndex];
            var predictedXYWH = tf.concat(new[] { predictedXY, predictedWH }, axis: -1);

            var predictedConf = tf.sigmoid(convRawConf);
            var predictedProb = tf.sigmoid(convRawProb);

            return tf.concat(new[] { predictedXYWH, predictedConf, predictedProb }, axis: -1);
        }

        static readonly int[] DefaultXYScale = { 1, 1, 1, };
        const float DefaultIntersectionOverUnionLossThreshold = 0.5f;
    }
}
