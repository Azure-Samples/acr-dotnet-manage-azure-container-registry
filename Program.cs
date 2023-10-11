// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.Resources.Models;
using Azure.ResourceManager.Samples.Common;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager;
using System.Net;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Compute;
using System.Net.NetworkInformation;
using Docker.DotNet;
using Azure.ResourceManager.ContainerRegistry.Models;
using Azure.ResourceManager.ContainerRegistry;
using System;

namespace ManageContainerRegistry
{
    public class Program
    {
        private static ResourceIdentifier? _resourceGroupId = null;


        /**
         * Azure Container Registry sample for managing container registry.
         *  - Create an Azure Container Registry to be used for holding the Docker images
         *  - If a local Docker engine cannot be found, create a Linux virtual machine that will host a Docker engine
         *      to be used for this sample
         *  - Use Docker DotNet to create a Docker client that will push/pull an image to/from Azure Container Registry
         *  - Pull a test image from the public Docker repo (hello-world:latest) to be used as a sample for pushing/pulling
         *      to/from an Azure Container Registry
         *  - Create a new Docker container from an image that was pulled from Azure Container Registry
         */
        public static async Task RunSample(ArmClient client)
        {
            string rgName = Utilities.CreateRandomName("ACRTemplateRG");
            string acrName = Utilities.CreateRandomName("acrsample");
            string saName = Utilities.CreateRandomName("sa");
            string dockerImageName = "hello-world";
            string dockerImageTag = "latest";
            string dockerContainerName = "sample-hello";
            string dockerImageRelPath = "samplesdotnet";

            try
            {
                // Get default subscription
                SubscriptionResource subscription = await client.GetDefaultSubscriptionAsync();

                // Create a resource group in the EastUS region
                Utilities.Log($"creating resource group...");
                ArmOperation<ResourceGroupResource> rgLro = await subscription.GetResourceGroups().CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(AzureLocation.EastUS));
                ResourceGroupResource resourceGroup = rgLro.Value;
                _resourceGroupId = resourceGroup.Id;
                Utilities.Log("Created a resource group with name: " + resourceGroup.Data.Name);

                //=============================================================
                // Create an Azure Container Registry to store and manage private Docker container images

                Utilities.Log("Creating an Azure Container Registry");

                var registryData = new ContainerRegistryData(resourceGroup.Data.Location, new ContainerRegistrySku(ContainerRegistrySkuName.Premium))
                {
                    IsAdminUserEnabled = true,
                    Sku = new ContainerRegistrySku(ContainerRegistrySkuName.Basic),
                    Tags =
                    {
                        { "key1","value1"},
                        { "key2","value2"}
                    }
                };
                var lro = await resourceGroup.GetContainerRegistries().CreateOrUpdateAsync(WaitUntil.Completed, acrName, registryData);
                ContainerRegistryResource containerRegistry = lro.Value;

                var acrCredentials = await containerRegistry.GetCredentialsAsync();

                //=============================================================
                // Create a Docker client that will be used to push/pull images to/from the Azure Container Registry

                using (DockerClient dockerClient = await DockerUtils.CreateDockerClient(resourceGroup))
                {
                    var pullImgResult = dockerClient.Images.PullImage(
                        new Docker.DotNet.Models.ImagesPullParameters()
                        {
                            Parent = dockerImageName,
                            Tag = dockerImageTag
                        },
                        new Docker.DotNet.Models.AuthConfig());

                    Utilities.Log("List Docker images for: " + dockerClient.Configuration.EndpointBaseUri.AbsoluteUri);
                    var listImages = dockerClient.Images.ListImages(
                        new Docker.DotNet.Models.ImagesListParameters()
                        {
                            All = true
                        });
                    foreach (var img in listImages)
                    {
                        Utilities.Log("\tFound image " + img.RepoTags[0] + " (id:" + img.ID + ")");
                    }

                    var createContainerResult = dockerClient.Containers.CreateContainer(
                        new Docker.DotNet.Models.CreateContainerParameters()
                        {
                            Name = dockerContainerName,
                            Image = dockerImageName + ":" + dockerImageTag
                        });
                    Utilities.Log("List Docker containers for: " + dockerClient.Configuration.EndpointBaseUri.AbsoluteUri);
                    var listContainers = dockerClient.Containers.ListContainers(
                        new Docker.DotNet.Models.ContainersListParameters()
                        {
                            All = true
                        });
                    foreach (var container in listContainers)
                    {
                        Utilities.Log("\tFound container " + container.Names[0] + " (id:" + container.ID + ")");
                    }

                    //=============================================================
                    // Commit the new container
                    
                    string privateRepoUrl = containerRegistry.Data.LoginServer + "/" + dockerImageRelPath + "/" + dockerContainerName;
                    Utilities.Log("Commiting image at: " + privateRepoUrl);

                    var commitContainerResult = dockerClient.Miscellaneous.CommitContainerChanges(
                        new Docker.DotNet.Models.CommitContainerChangesParameters()
                        {
                            ContainerID = dockerContainerName,
                            RepositoryName = privateRepoUrl,
                            Tag = dockerImageTag
                        });

                    //=============================================================
                    // Push the new Docker image to the Azure Container Registry

                    var pushImageResult = dockerClient.Images.PushImage(privateRepoUrl,
                        new Docker.DotNet.Models.ImagePushParameters()
                        {
                            ImageID = privateRepoUrl,
                            Tag = dockerImageTag
                        },
                        new Docker.DotNet.Models.AuthConfig()
                        {
                            Username = acrCredentials.Value.Username,
                            Password = acrCredentials.Value.Passwords.First().Value,
                            ServerAddress = containerRegistry.Data.LoginServer
                        });

                    //=============================================================
                    // Verify that the image we saved in the Azure Container registry can be pulled and instantiated locally

                    var pullAcrImgResult = dockerClient.Images.PullImage(
                        new Docker.DotNet.Models.ImagesPullParameters()
                        {
                            Parent = privateRepoUrl,
                            Tag = dockerImageTag
                        },
                        new Docker.DotNet.Models.AuthConfig()
                        {
                            Username = acrCredentials.Value.Username,
                            Password = acrCredentials.Value.Passwords.First().Value,
                            ServerAddress = containerRegistry.Data.LoginServer
                        });

                    Utilities.Log("List Docker images for: " + dockerClient.Configuration.EndpointBaseUri.AbsoluteUri);
                    listImages = dockerClient.Images.ListImages(
                        new Docker.DotNet.Models.ImagesListParameters()
                        {
                            All = true
                        });
                    foreach (var img in listImages)
                    {
                        Utilities.Log("\tFound image " + img.RepoTags[0] + " (id:" + img.ID + ")");
                    }

                    var createAcrContainerResult = dockerClient.Containers.CreateContainer(
                        new Docker.DotNet.Models.CreateContainerParameters()
                        {
                            Name = dockerContainerName + "fromazure",
                            Image = privateRepoUrl + ":" + dockerImageTag
                        });

                    Utilities.Log("List Docker containers for: " + dockerClient.Configuration.EndpointBaseUri.AbsoluteUri);
                    listContainers = dockerClient.Containers.ListContainers(
                        new Docker.DotNet.Models.ContainersListParameters()
                        {
                            All = true
                        });
                    foreach (var container in listContainers)
                    {
                        Utilities.Log("\tFound container " + container.Names[0] + " (id:" + container.ID + ")");
                    }
                }
            }
            finally
            {
                try
                {
                    if (_resourceGroupId is not null)
                    {
                        Utilities.Log($"Deleting Resource Group: {_resourceGroupId}");
                        await client.GetResourceGroupResource(_resourceGroupId).DeleteAsync(WaitUntil.Completed);
                        Utilities.Log($"Deleted Resource Group: {_resourceGroupId}");
                    }
                }
                catch (Exception)
                {
                    Utilities.Log("Did not create any resources in Azure. No clean up is necessary");
                }
            }
        }

        public static async Task Main(string[] args)
        {
            try
            {
                //=================================================================
                // Authenticate
                var clientId = Environment.GetEnvironmentVariable("CLIENT_ID");
                var clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
                var tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
                var subscription = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
                ClientSecretCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                ArmClient client = new ArmClient(credential, subscription);

                await RunSample(client);
            }
            catch (Exception ex)
            {
                Utilities.Log(ex);
            }
        }
    }
}